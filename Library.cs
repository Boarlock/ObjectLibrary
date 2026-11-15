using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace ObjectLibrary {

    public readonly record struct KeyedEntry(EnumKey Key, object Value);
    public sealed class Entry<TType>(TType value) { public TType Value { get; } = value; }

    internal readonly record struct Slot(
        EnumKey Key, 
        object Value, 
        DateTimeOffset Recent) {

        public bool IsExpired(
            DateTimeOffset now, 
            TimeSpan timeToExpire) 
            => now - Recent >= timeToExpire;
    }

    // Enum keys are identical by handle and value, aliases compare as equal.
    public readonly record struct EnumKey(
        RuntimeTypeHandle TypeHandle,
        ulong Value) {
        public static EnumKey Create<TEnum>(TEnum value)
            where TEnum : struct, Enum {

            // Unsafe.As converts the enum to its underlying integral type with zero boxing
            ulong rawValue = Unsafe.SizeOf<TEnum>() switch {

                1 => Unsafe.As<TEnum, byte>(ref value),
                2 => Unsafe.As<TEnum, ushort>(ref value),
                4 => Unsafe.As<TEnum, uint>(ref value),
                8 => Unsafe.As<TEnum, ulong>(ref value),
                _ => throw new NotSupportedException()
            };

            return new EnumKey(typeof(TEnum).TypeHandle, rawValue);
        }
    }

    public enum Result {

        Success,
        AlreadyExist,
        KeyNotFound,
        Full
    }

    public class Library {

        public Library(
            int? initialCapacity = null,
            TimeSpan? timeToExpire = null,
            bool? overwriteOldEntries = null) {

            _overwriteOld = overwriteOldEntries ?? false;
            _capacity = initialCapacity ?? int.MaxValue;

            var t = timeToExpire ?? TimeSpan.MaxValue;
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(t, TimeSpan.Zero);
            _timeToExpire = t;
        }

        private readonly object syncRoot = new();

        public event Action<KeyedEntry>? OnExpire;
        public event Action<KeyedEntry>? OnEvict;

        private readonly LinkedList<Slot> priorities = new();
        private readonly Dictionary<EnumKey, LinkedListNode<Slot>> values = new();

        private readonly TimeSpan _timeToExpire;
        private readonly TimeProvider _time = TimeProvider.System;

        private volatile bool _overwriteOld;
        public bool OverwriteOld {

            get => _overwriteOld;
            set => _overwriteOld = value;
        }

        private int _capacity;
        public int Capacity {

            get => Volatile.Read(ref _capacity);
            set {

                List<Slot>? expired = null;
                List<Slot>? evicted = null;

                try {

                    ArgumentOutOfRangeException.ThrowIfNegative(value);

                    lock (syncRoot) {

                        expired = SweepExpired();

                        if (values.Count > value &&
                            !_overwriteOld)
                            throw new ArgumentException("Cannot lower capacity below count when OverwriteOld is set to false.", nameof(value));

                        while (values.Count > value) {

                            LinkedListNode<Slot> oldNode = priorities.First!;

                            (evicted ??= []).Add(oldNode.Value);
                            values.Remove(oldNode.Value.Key);
                            priorities.Remove(oldNode);
                        }

                        _capacity = value;
                    }
                }
                finally {

                    RaiseExpired(expired);
                    RaiseEvicted(evicted);
                }
            }
        }
        
        public int Count { 

            get {

                List<Slot>? expired = null;

                try {

                    lock (syncRoot) {

                        expired = SweepExpired();

                        return values.Count;
                    }
                }
                finally {

                    RaiseExpired(expired);
                }
            }
        }

        private List<Slot>? SweepExpired() {

            if (_timeToExpire == TimeSpan.MaxValue)
                return null;

            List<Slot>? expired = null;
            var now = _time.GetUtcNow();

            while (priorities.First is { } head && 
                head.Value.IsExpired(now, _timeToExpire)) {

                values.Remove(head.Value.Key);
                priorities.RemoveFirst();

                (expired ??= []).Add(head.Value);
            }

            return expired;
        }

        private static void InvokeAll(
            Action<KeyedEntry>? handlers, 
            KeyedEntry arg) {

            if (handlers is null)
                return;

            foreach (Delegate d in handlers.GetInvocationList()) {

                try { ((Action<KeyedEntry>)d)(arg); }
                catch { /* isolate each subscriber */ }
            }
        }

        private void RaiseExpired(List<Slot>? expired) {

            if (expired is null)
                return;

            var handlers = OnExpire;

            foreach (Slot slot in expired)
                InvokeAll(handlers, new KeyedEntry(slot.Key, slot.Value));
        }

        private void RaiseEvicted(List<Slot>? expired) {

            if (expired is null)
                return;

            var handlers = OnEvict;

            foreach (Slot slot in expired)
                InvokeAll(handlers, new KeyedEntry(slot.Key, slot.Value));
        }

        private void MoveToEnd(LinkedListNode<Slot> node) {

            priorities.Remove(node);
            priorities.AddLast(node);
        }

        // Snapshot gets entries from oldest to newest (i.e eviction order)
        public List<KeyedEntry> GetSnapshot() {

            List<KeyedEntry> entries = new();
            List<Slot>? expired = null;

            try {

                lock (syncRoot) {

                    expired = SweepExpired();

                    foreach (Slot slot in priorities) {

                        entries.Add(new KeyedEntry(slot.Key, slot.Value));
                    }
                }
            }
            finally {

                RaiseExpired(expired);
            }

            return entries;
        }

        // Add with optional option to replace
        public bool TryAdd<TKey, TType>(
            TKey k,
            Entry<TType> entry,
            bool overwrite = false) where TKey : struct, Enum
            => TryAddResult(k, entry, overwrite) == Result.Success;

        public Result TryAddResult<TKey, TType>(
            TKey k,
            Entry<TType> entry,
            bool overwrite = false) where TKey : struct, Enum {

            List<Slot>? expired = null;
            List<Slot>? evicted = null;
            Result result;

            try {

                ArgumentNullException.ThrowIfNull(entry);
                var key = EnumKey.Create(k);

                lock (syncRoot) {

                    expired = SweepExpired();

                    var now = _time.GetUtcNow();
                    bool exists = values.TryGetValue(key, out var node);
                    bool full = values.Count >= _capacity;

                    // Key taken, caller didn't ask to replace it
                    if (exists && !overwrite) {

                        result = Result.AlreadyExist;
                    }
                    // Key taken, replace it in place (count doesn't change, so no capacity check)
                    else if (exists) {

                        // Re-adding the same Entry isn't a displacement
                        if (!ReferenceEquals(node!.Value.Value, entry))
                            (evicted ??= []).Add(node.Value);

                        node.Value = new Slot(key, entry, now);
                        MoveToEnd(node);

                        result = Result.Success;
                    }
                    // Full and not allowed to evict (also covers capacity 0)
                    else if (full && (!_overwriteOld || priorities.First is null)) {

                        result = Result.Full;
                    }
                    // Full, evict the oldest and reuse its node
                    else if (full) {

                        var oldNode = priorities.First!;
                        (evicted ??= []).Add(oldNode.Value);

                        values.Remove(oldNode.Value.Key);
                        oldNode.Value = new Slot(key, entry, now);
                        values.Add(key, oldNode);
                        MoveToEnd(oldNode);
                        result = Result.Success;
                    }
                    // Room available
                    else {

                        values.Add(key, priorities.AddLast(new Slot(key, entry, now)));
                        result = Result.Success;
                    }
                }

                return result;
            }
            finally {

                RaiseExpired(expired);
                RaiseEvicted(evicted);
            }
        }

        // Safely get while removing
        public bool TryWithdraw<TKey, TType>(
            TKey k,
            [MaybeNullWhen(false)]  out Entry<TType> entry) where TKey : struct, Enum
            => TryWithdrawResult(k, out entry) == Result.Success;

        public Result TryWithdrawResult<TKey, TType>(
            TKey k,
            out Entry<TType> entry) where TKey : struct, Enum {

            entry = null!;
            List<Slot>? expired = null;

            try {

                lock (syncRoot) {

                    expired = SweepExpired();

                    if (!values.TryGetValue(EnumKey.Create(k), out LinkedListNode<Slot>? node))
                        return Result.KeyNotFound;

                    if (node.Value.Value is not Entry<TType> found)
                        throw new ArgumentException($"Entry under this key is {node.Value.Value.GetType()}, not {typeof(Entry<TType>)}.");

                    values.Remove(node.Value.Key);
                    priorities.Remove(node);

                    entry = found;
                    return Result.Success;
                }
            }
            finally {

                RaiseExpired(expired);
            }
        }

        public bool TryUpdateOrAdd<TKey, TType>(
            TKey k,
            Entry<TType> entry) where TKey : struct, Enum
            => TryUpdateOrAddResult(k, entry) == Result.Success;

        public Result TryUpdateOrAddResult<TKey, TType>(
            TKey k,
            Entry<TType> entry) where TKey : struct, Enum
            => TryAddResult(k, entry, overwrite: true);

        public bool ContainsKey<TKey>(
            TKey k) where TKey : struct, Enum {

            List<Slot>? expired = null;

            try {

                lock (syncRoot) {

                    expired = SweepExpired();

                    return values.ContainsKey(EnumKey.Create(k));
                }
            }
            finally {

                RaiseExpired(expired);
            }
        }

        public bool Discard<TKey>(
            TKey k) where TKey : struct, Enum {

            List<Slot>? expired = null;

            try {

                lock (syncRoot) {

                    expired = SweepExpired();

                    if (!values.TryGetValue(EnumKey.Create(k), out LinkedListNode<Slot>? node))
                        return false;

                    values.Remove(node.Value.Key);
                    priorities.Remove(node);
                    return true;
                }
            }
            finally {

                RaiseExpired(expired);
            }
        }

        public void Clear() {

            List<Slot>? expired = null;

            try {

                lock (syncRoot) {

                    expired = SweepExpired();

                    priorities.Clear();
                    values.Clear();
                }
            }
            finally {

                RaiseExpired(expired);
            }
        }
    }
}
