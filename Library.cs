using System.Collections;
using System.Runtime.CompilerServices;

namespace ObjectLibrary {

    public sealed class Entry<TType>(TType value) {

        public TType Value { get; } = value;
    }

    public readonly record struct Slot(
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
        WrongKey,
        Full
    }

    public class Library(
        int? initialCapacity = null, 
        TimeSpan? timeToExpire = null) 
        : IEnumerable<Slot> {

        private readonly object syncRoot = new();

        private readonly LinkedList<Slot> priorities = new();
        private readonly Dictionary<EnumKey, LinkedListNode<Slot>> values = new();

        private readonly TimeSpan _timeToExpire = timeToExpire ?? TimeSpan.MaxValue;
        private readonly TimeProvider _time = TimeProvider.System;

        private int capacity = initialCapacity ?? int.MaxValue;
        public int Capacity { 

            get {

                List<Slot>? expired = null;

                try {

                    lock (syncRoot) {

                        expired = SweepExpired();

                        return capacity;
                    }
                }
                finally {

                    Raise(expired);
                }
            }
            set {

                List<Slot>? expired = null;

                try {

                    ArgumentOutOfRangeException.ThrowIfNegative(value);

                    lock (syncRoot) {

                        expired = SweepExpired();

                        while (values.Count > value) {

                            LinkedListNode<Slot> oldNode = priorities.First!;

                            values.Remove(oldNode.Value.Key);
                            priorities.Remove(oldNode);
                        }

                        capacity = value;
                    }
                }
                finally {

                    Raise(expired);
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

                    Raise(expired);
                }
            }
        }

        public event Action<object>? OnExpire;

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public IEnumerator<Slot> GetEnumerator() {

            Slot[] snapshot;
            List<Slot>? expired = null;

            try {

                lock (syncRoot) {

                    expired = SweepExpired();

                    snapshot = new Slot[priorities.Count];
                    priorities.CopyTo(snapshot, 0);
                }

                return ((IEnumerable<Slot>)snapshot).GetEnumerator();
            }
            finally {

                Raise(expired);
            }
        }

        public int EnsureCapacity(int capacity) {

            List<Slot>? expired = null;

            try {

                ArgumentOutOfRangeException.ThrowIfNegative(capacity);

                lock (syncRoot) {

                    expired = SweepExpired();

                    if (this.capacity < capacity)
                        this.capacity = capacity;

                    return this.capacity;
                }
            }
            finally {

                Raise(expired);
            }
        }

        public void TrimExcess() {

            List<Slot>? expired = null;

            try {

                lock (syncRoot) {

                    expired = SweepExpired();

                    capacity = values.Count;
                }
            }
            finally {

                Raise(expired);
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

        private void Raise(List<Slot>? expired) {

            if (expired is null or { Count: 0 })
                return;

            var handler = OnExpire;
            if (handler is null)
                return;

            foreach (Slot slot in expired) {

                try {

                    handler(slot.Value);
                }
                catch {

                    // one bad subscriber shouldn't stop the rest or replace an in-flight exception
                }
            }
        }

        private void MoveToEnd(LinkedListNode<Slot> node) {

            priorities.Remove(node);
            priorities.AddLast(node);
        }

        public bool TryStore<TKey, TType>(
            TKey k,
            Entry<TType> entry,
            bool overwrite = false) where TKey : struct, Enum
            => TryStoreResult(k, entry, overwrite) == Result.Success;

        public Result TryStoreResult<TKey, TType>(
            TKey k,
            Entry<TType> entry,
            bool overwrite = false) where TKey : struct, Enum {

            List<Slot>? expired = null;

            try {

                ArgumentNullException.ThrowIfNull(entry);
                var key = EnumKey.Create(k);

                lock (syncRoot) {

                    expired = SweepExpired();

                    // Existing key
                    if (values.TryGetValue(key, out var node)) {

                        if (!overwrite)
                            return Result.AlreadyExist;

                        node.Value = new Slot(key, entry, _time.GetUtcNow());
                        MoveToEnd(node);
                        return Result.Success;
                    }

                    // Full (also covers capacity 0, where there is nothing to evict)
                    if (values.Count >= capacity) {

                        if (!overwrite || priorities.First is null)
                            return Result.Full;

                        var oldNode = priorities.First;

                        values.Remove(oldNode.Value.Key);
                        oldNode.Value = new Slot(key, entry, _time.GetUtcNow());
                        values.Add(key, oldNode);
                        MoveToEnd(oldNode);
                        return Result.Success;
                    }

                    // Room available
                    values.Add(key, priorities.AddLast(new Slot(key, entry, _time.GetUtcNow())));
                    return Result.Success;
                }
            }
            finally {

                Raise(expired);
            }
        }

        public bool TryWithdraw<TKey, TType>(
            TKey k,
            out Entry<TType> entry) where TKey : struct, Enum
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
                        return Result.WrongKey;

                    if (node.Value.Value is not Entry<TType> found)
                        throw new ArgumentException($"Entry under this key is {node.Value.Value.GetType()}, not {typeof(Entry<TType>)}.");

                    values.Remove(node.Value.Key);
                    priorities.Remove(node);

                    entry = found;
                    return Result.Success;
                }
            }
            finally {

                Raise(expired);
            }
        }

        public object? TryPeek<TKey>( 
            TKey k) where TKey : struct, Enum {

            List<Slot>? expired = null;

            try {

                lock (syncRoot) {

                    expired = SweepExpired();

                    if (!values.TryGetValue(EnumKey.Create(k), out LinkedListNode<Slot>? node))
                        return default;

                    return node.Value.Value;
                }
            }
            finally {

                Raise(expired);
            }
        }

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

                Raise(expired);
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

                Raise(expired);
            }
        }

        // Doesn't need an expiry event fire, thats for natural expiration not user clearing.
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

                Raise(expired);
            }
        }
    }
}
