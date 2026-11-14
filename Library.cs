using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace ObjectLibrary {

    public sealed class Entry<TType> {

        public TType Value { get; } // Immutable

        public Entry(TType value) {

            Value = value;
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
        WrongKey,
        AlreadyExist,
        Failed,
        Full
    }

    public class Library {

        public Library(int? capacity = null) => _capacity = capacity ?? -1;

        private sealed record State(int Capacity, int Count);
        private readonly ConcurrentDictionary<EnumKey, object> database = [];

        public int Capacity {

            get => Volatile.Read(ref _capacity);
            set => Volatile.Write(ref _capacity, value);
        }

        public int Count => Volatile.Read(ref _count);

        private int _capacity = -1;   // -1 = unbounded
        private int _count;

        public event Action? OnEviction;

        // Atomically claim a slot, or fail if full
        private bool TryReserve() {

            while (true) {

                int count = Volatile.Read(ref _count);
                int cap = Volatile.Read(ref _capacity);

                if (cap >= 0 && count >= cap)
                    return false;

                if (Interlocked.CompareExchange(ref _count, count + 1, count) == count)
                    return true;
            }
        }

        private void Release() => Interlocked.Decrement(ref _count);

        public bool TryStore<TKey, TType>(
            TKey k,
            Entry<TType> entry,
            bool overwrite = false) where TKey : struct, Enum
            => TryStoreResult(k, entry, overwrite) == Result.Success;

        public Result TryStoreResult<TKey, TType>(
            TKey k,
            Entry<TType> entry,
            bool overwrite = false) where TKey : struct, Enum {

            ArgumentNullException.ThrowIfNull(entry);
            var key = EnumKey.Create(k);

            while (true) {

                if (overwrite && database.TryGetValue(key, out var existing)) {

                    if (database.TryUpdate(key, entry, existing)) // Overwrite count doesn't need changed
                        return Result.Success;

                    continue;
                }

                if (!TryReserve()) // If false is full, otherwise count is updated
                    return Result.Full;

                if (database.TryAdd(key, entry)) // If false key already exists
                    return Result.Success;

                Release(); // Decrement count as it was already updated elsewhere

                if (!overwrite) // If no overwrite return, otherwise loop back (might be redundant)
                    return Result.AlreadyExist;
            }
        }

        public bool TryWithdraw<TKey, TType>(
            TKey k,
            out Entry<TType> entry) where TKey : struct, Enum
            => TryWithdrawResult(k, out entry) == Result.Success;

        public Result TryWithdrawResult<TKey, TType>(
            TKey k,
            out Entry<TType> entry) where TKey : struct, Enum {

            var key = EnumKey.Create(k);
            entry = null!;

            if (!database.TryGetValue(key, out var o))
                return Result.WrongKey;

            if (o is not Entry<TType> found)
                throw new ArgumentException($"Entry under this key is {o.GetType()}, not {typeof(Entry<TType>)}.");

            if (!database.TryRemove(new KeyValuePair<EnumKey, object>(key, o)))
                return Result.Failed;

            Release();
            entry = found;
            return Result.Success;
        }

        public object? TryPeek<TKey>( 
            TKey k) where TKey : struct, Enum {

            var key = EnumKey.Create(k);

            if (database.TryGetValue(key, out var entry))
                return entry;

            return null;
        }

        public bool Contains<TKey>(
            TKey k) where TKey : struct, Enum {

            return database.ContainsKey(EnumKey.Create(k));
        }

        public bool Discard<TKey>(
            TKey k) where TKey : struct, Enum {

            if (database.TryRemove(EnumKey.Create(k), out _)) {

                Release();
                return true;
            }

            return false;
        }

        public bool Replace<TKey, TValue>(
            TKey k,
            Entry<TValue> entry) where TKey : struct, Enum
            => ReplaceResult(k, entry) == Result.Success;

        public Result ReplaceResult<TKey, TValue>( 
            TKey k,
            Entry<TValue> entry) where TKey : struct, Enum {

            ArgumentNullException.ThrowIfNull(entry);
            var key = EnumKey.Create(k);

            if (!database.TryGetValue(key, out var old))
                return Result.WrongKey;

            return database.TryUpdate(key, entry, old) ? Result.Success : Result.Failed;
        }

        public void Clear() {

            foreach (var kv in database)
                if (database.TryRemove(kv))
                    Release();
        }
    }
}
