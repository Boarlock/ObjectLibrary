using System;
using System.Collections;
using System.Runtime.CompilerServices;

namespace ObjectLibrary {

    public sealed class Entry<TType>(TType value) {

        public TType Value { get; } = value;


    }

    internal readonly record struct Slot(
        EnumKey Key, 
        object Value, 
        DateTimeOffset Recent) : IDisposable {

        public bool IsExpired(
            TimeProvider provider, 
            TimeSpan timeToExpire) 
            => provider.GetUtcNow() >= Recent + timeToExpire;

        public void Dispose() {


        }
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

                lock (syncRoot)
                    return capacity;
            }
            set {

                ArgumentOutOfRangeException.ThrowIfNegative(value);

                lock (syncRoot) {

                    while (values.Count > value) {

                        LinkedListNode<Slot> oldNode = priorities.First!;

                        values.Remove(oldNode.Value.Key);
                        priorities.Remove(oldNode);
                    }

                    capacity = value;
                }
            }
        }
        
        public int Count { 

            get {

                lock (syncRoot)
                    return values.Count;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public IEnumerator<Slot> GetEnumerator() {

            Slot[] snapshot;

            lock (syncRoot) {

                snapshot = new Slot[priorities.Count];
                priorities.CopyTo(snapshot, 0);
            }

            return ((IEnumerable<Slot>)snapshot).GetEnumerator();
        }
        public int EnsureCapacity(int capacity) {

            ArgumentOutOfRangeException.ThrowIfNegative(capacity);

            lock (syncRoot) {

                if (this.capacity < capacity)
                    this.capacity = capacity;

                return this.capacity;
            }
        }

        public void TrimExcess() {

            lock (syncRoot) {

                capacity = values.Count;
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
            bool overwrite = true) where TKey : struct, Enum {

            ArgumentNullException.ThrowIfNull(entry);
            var key = EnumKey.Create(k);

            lock (syncRoot) {

                // Existing key
                if (values.TryGetValue(key, out var node)) {

                    if (!overwrite)
                        return Result.AlreadyExist;

                    node.Value = new Slot(key, entry);
                    MoveToEnd(node);
                    return Result.Success;
                }

                // Full (also covers capacity 0, where there is nothing to evict)
                if (values.Count >= capacity) {

                    if (!overwrite || priorities.First is null)
                        return Result.Full;

                    var oldNode = priorities.First;

                    values.Remove(oldNode.Value.Key);
                    oldNode.Value = new Slot(key, entry);
                    values.Add(key, oldNode);
                    MoveToEnd(oldNode);
                    return Result.Success;
                }

                // Room available
                values.Add(key, priorities.AddLast(new Slot(key, entry)));
                return Result.Success;
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

            lock (syncRoot) {

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

        public object? TryPeek<TKey>( 
            TKey k) where TKey : struct, Enum {

            lock (syncRoot) {

                if (!values.TryGetValue(EnumKey.Create(k), out LinkedListNode<Slot>? node))
                    return default;

                return node.Value.Value;
            }
        }

        public bool ContainsKey<TKey>(
            TKey k) where TKey : struct, Enum {

            lock (syncRoot)
                return values.ContainsKey(EnumKey.Create(k));
        }

        public bool Discard<TKey>(
            TKey k) where TKey : struct, Enum {

            lock (syncRoot) {

                if (!values.TryGetValue(EnumKey.Create(k), out LinkedListNode<Slot>? node))
                    return false;

                values.Remove(node.Value.Key);
                priorities.Remove(node);
                return true;
            }
        }

        public void Clear() {

            lock (syncRoot) {

                priorities.Clear();
                values.Clear();
            }
        }
    }
}
