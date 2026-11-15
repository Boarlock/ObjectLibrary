# ObjectLibrary

## Overview

ObjectLibrary is a lightweight, thread-safe object storage library for .NET. It provides a centralized location to store and retrieve strongly typed objects using enum keys, with optional capacity limits, automatic expiration, and eviction policies.

The library supports multiple object types within a single instance without requiring the library itself to be generic.

## Features

- Heterogeneous storage: Store different object types in the same library
- Strongly typed entries: Generic `Entry<TType>` wrappers preserve the stored type
- Enum-based keys: Use different enum types as independent keys
- No boxing enum keys: Convert enum values into internal keys without boxing
- Optional capacity limits: Restrict number of stored entries
- Optional Automatic expiration: Configure a duration after which entries expire
- Eviction notification: Receive notification when entries are displaced
- Thread-safe operations: Locks keep data safe across multi-threaded operations
- Snapshot enumeration: Retrieve a snapshot of stored entries without exposing internal collections
- Result returning overloads: Choose between simple boolean results and detailed operation results

## Usage example

```csharp
// Store different object types
library.TryAdd(
	WeaponKey.Primary,
	new Entry<Weapon>(new Weapon()));

library.TryAdd(
	ItemKey.HealthPotion,
	new Entry<Potion>(new Potion()));

// Retrieve an object
if (library.TryWithdraw(
	WeaponKey.Primary,
	out Entry<Weapon>? weapon))
{
	Console.WriteLine("Weapon retrieved!");
}

// Inspect stored entries
List<KeyedEntry> snapshot = library.GetSnapshot();
```

### Entry<TType>

A strongly typed wrapper around a stored value:

```csharp
public sealed class Entry<TType>(TType value)
{
	public TType Value { get; } = value;
}
```

The library uses this wrapper to retain type information while supporting heterogeneous storage.

Example:
```csharp
var sword = new Entry<Sword>(new Sword());
var swordDesc = new Entry<string>("Sword is very sharp!");
```

### KeyedEntry

A non-generic representation of a stored entry, primarily used for snapshots and event notifications.

```csharp
public readonly record struct KeyedEntry(
	EnumKey Key,
	object Value);
```

### EnumKey

A normalized representation of an enum key.

```csharp
public readonly record struct EnumKey(
	RuntimeTypeHandle TypeHandle,
	ulong Value);
```

Its EnumKey.Create<TEnum>() method converts an enum into its internal representation without boxing.

Different enum types remain distinct even when their underlying values are identical. Enum aliases with identical values compare as equal.

### Result

The result returned by detailed storage operations.

```csharp
public enum Result
{
	Success,
	AlreadyExist,
	KeyNotFound,
	Full
}
```

- Success: Operation completed successfully
- AlreadyExist: The key is already occupied and overwrite wasn't requested
- KeyNotFound: No matching key exists
- Full: Capacity has been reached and eviction isn't permitted

### Operations

#### Adding entries

`TryAdd<TKey, TType>()`

```csharp
public bool TryAdd<TKey, TType>(
	TKey k,
	Entry<TType> entry,
	bool overwrite = false)
	where TKey : struct, Enum;
```

Attempts to store an entry under the supplied enum key.

- Returns `true` when successful
- Returns `false` when the operation cannot be completed
- Optionally replaces an existing entry using `overwrite`
- Throws `ArgumentNullException` for a null entry

`TryAddResult<TKey, TType>()`

Same operation, but returns a detailed `Result`.

#### Updating or adding entries

`TryUpdateOrAdd<TKey, TType>()`

```csharp
public bool TryUpdateOrAdd<TKey, TType>(
	TKey k,
	Entry<TType> entry)
	where TKey : struct, Enum;
```

Attempts to add an entry or replace the existing entry under the supplied key. Since this method delegates to `TryAdd()`, this method throws `ArgumentNullException` for a null entry.

`TryUpdateOrAddResult<TKey, TType>()`

Same operation, returning a detailed `Result`.

#### Withdrawing entries

`TryWithdraw<TKey, TType>()`

```csharp
public bool TryWithdraw<TKey, TType>(
	TKey k,
	out Entry<TType> entry)
	where TKey : struct, Enum;
```

Attempts to retrieve and remove an entry.

- Returns `true` when successful
- Returns `false` when the key doesn't exist
- Throws `ArgumentException` when the key exists but contains an incompatible entry type
- `Entry<TType> entry` is null when returning false

`TryWithdrawResult<TKey, TType>()`

Same operation, returning a detailed `Result`. Likewise, on this overload `Entry<TType> entry` is null when returning anything other than Result.Success.

#### Removing entries

`Discard<TKey>()`

```csharp
public bool Discard<TKey>(
	TKey k)
	where TKey : struct, Enum;
```

Removes an entry without returning it.

`Clear()`

```csharp
public void Clear()
```

Removes all stored entries. It fires `OnExpire` before clearing the internal collections for any item that have expired, it does not fire `OnEvict` for any entries removed.

### Other methods

`ContainsKey<TKey>()`

```csharp
public bool ContainsKey<TKey>(
	TKey k) 
	where TKey : struct, Enum;
```

A point-in-time answer that says whether a specified key is present. Not atomic so another thread can remove the object before acting on this information.

`GetSnapshot()`

```csharp
public List<KeyedEntry> GetSnapshot()
```

Returns a snapshot of the current entries, ordered from oldest to newest (eviction order).

The list is independent, but each `KeyedEntry.Value` is the same underlying `Entry` object the library holds. Like other methods it sweeps expired entries beforehand.

### Configuration and behavior

| Configuration | Default | Behavior |
|---|---|---|
| `Capacity` | `int.MaxValue` | Effectively unlimited capacity |
| `Expiration` | Disabled | Entries remain until explicitly removed or evicted |
| Overwrite old entries | `false` | Full libraries reject new entries unless `OverwriteOld` is enabled. |
| Expiration event | No subscribers | No notification unless subscribed |
| Eviction event | No subscribers | No notification unless subscribed |

#### Capacity management

When capacity is reached:
- With `OverwriteOld = false`, new entries are rejected with `Result.Full`.
- With `OverwriteOld = true`, the oldest entry is removed to make room.
- Reducing `Capacity` below the current count requires `OverwriteOld = true`; otherwise, an `ArgumentException` is thrown
- Evicted entries trigger `OnEvict`

#### Expiration

When expiration is configured, the library checks for expired entries during operations that perform expiration sweeps.

Expired entries are removed and reported through `OnExpire`.

Expiration is **lazy**: There is no background timer or dedicated cleanup thread. An expired entry is cleaned up when a subsequent operation triggers a sweep.

### API Reference

#### `Library`

The main storage class. Each instance maintains its own collection of entries and configuration.

```csharp
public Library(
	int? initialCapacity = null,
	TimeSpan? timeToExpire = null,
	bool? overwriteOldEntries = null)
```

| Parameter | Description |
|---|---|
| `initialCapacity` | Maximum number of entries. Defaults to `int.MaxValue`. |
| `timeToExpire` | 	Maximum entry lifetime. Defaults to `TimeSpan.MaxValue` (effectively disabled). Throws `ArgumentOutOfRangeException` when a zero or negative value is given. |
| `overwriteOldEntries` | Whether the library may evict existing entries when full. Defaults to `false`. |

| Properties | Type | Description |
|---|---|---|
| `Capacity` | `int` | Gets or sets the maximum number of entries. Throws `ArgumentOutOfRangeException` for a negative value. |
| `Count` | `int` | Gets the number of currently stored entries. |
| `OverwriteOld` | `bool` | Controls whether the oldest entry can be evicted when capacity is reached. |

| Events | Type | Description |
|---|---|---|
| `OnExpire` | `Action<KeyedEntry>` | Invoked for entries removed due to expiration. |
| `OnEvict` | `Action<KeyedEntry>` | Invoked for entries displaced by TryAdd.. methods when `overwrite = true` or capacity reduction when `Count > newCapacity`. |

### Other details

**Expiration**

- Expiration is measured from when an entry was added or replaced (overwrites reset it).
- It is approximate (timer resolution is around 10-15 ms on Windows).
- Expired entries stay in memory until the next call into the library (except noted below).
- The `Capacity` getter and `OverwriteOld` setter/getter do not initiate an expiration sweep.

**Events**

- Events run on the thread that triggered the sweep or eviction.
- They run after the lock is released so handlers can safely call back into the library.
- Each subscriber is isolated, and exceptions from handlers are swallowed.
- Subscribers must not dispose the received object unless they own it, since the caller may still hold a reference.

**Thread safety scope**

- The library's own state is protected but the object stored inside entries aren't.

