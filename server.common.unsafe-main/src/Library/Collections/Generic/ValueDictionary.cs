using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;
using HUnsafe = System.Runtime.CompilerServices.Unsafe;

namespace Server.Common.Unsafe.Collections.Generic;


public ref struct ValueDictionary<TKey, TValue>
                                                where TKey : unmanaged
                                                where TValue : unmanaged
{
    #region Вложенные типы

    private struct Entry
    {
        public uint HashCode;
        /// <summary>
        /// 0-based index of next entry in chain: -1 means end of chain
        /// also encodes whether this entry _itself_ is part of the free list by changing sign and subtracting 3,
        /// so -2 means end of free list, -3 means index 0 but on free list, -4 means index 1 but on free list, etc.
        /// </summary>
        public int Next;
        public TKey Key;     // Key of entry
        public TValue Value; // Value of entry
    }

    #endregion Вложенные типы


    #region Особое

    public TValue this[TKey key]
    {
        get
        {
            ref TValue value = ref FindValue(key);
            if (!HUnsafe.IsNullRef(ref value))
            {
                return value;
            }

            throw new KeyNotFoundException(key.ToString());
        }
        set
        {
            bool modified = TryInsert(key, value, InsertionBehavior.OverwriteExisting);
            Debug.Assert(modified);
        }
    }

    #endregion Особое


    #region Поля

    private readonly Span<int> _buckets;
    private readonly Span<Entry> _entries;
    private int _count;
    private int _freeList;
    private int _freeCount;

    private const int StartOfFreeList = -3;

    #endregion Поля


    #region Конструкторы

    public ValueDictionary() : this(0)
    { }


    public ValueDictionary(byte capacity)
    {
        IsZeroCapacity = capacity == 0;

        if (IsZeroCapacity)
        {
            _buckets = Span<int>.Empty;
            _entries = Span<Entry>.Empty;

            _count = 0x0;
            _freeList = 0x0;
            _freeCount = 0x0;
        }
        else
        {
            int size = HashHelpers.GetPrime(capacity);

            // Assign member variables after both arrays allocated to guard against corruption from OOM if second fails
            _freeList = -1;

            unsafe
            {
                int* bucketsPtr = stackalloc int[size];
                Entry* entriesPtr = stackalloc Entry[size];

                _buckets = new Span<int>(bucketsPtr, size);
                _entries = new Span<Entry>(entriesPtr, size);
            }

            _count = 0;
            _freeCount = 0;
        }
    }

    #endregion Конструкторы


    #region Свойства

    public byte Count => IsZeroCapacity ? (byte)0 : (byte)(_count - _freeCount);

    
    public bool IsZeroCapacity { get; }


    public static ValueDictionary<TKey, TValue> ZeroCapacity => new();

    #endregion Свойства


    #region Методы

    public void Clear()
    {
        var count = _count;
        if (IsZeroCapacity || count == 0)
            return;

        _buckets.Clear();
        _entries.Clear();

        _count = 0;
        _freeList = -1;
        _freeCount = 0;
    }


    public void Add(TKey key, TValue value)
    {
        bool modified = TryInsert(key, value, InsertionBehavior.ThrowOnExisting);
        Debug.Assert(modified); // If there was an existing key and the Add failed, an exception will already have been thrown.
    }


    public bool ContainsKey(TKey key) =>
        !HUnsafe.IsNullRef(ref FindValue(key));


    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        ref TValue valRef = ref FindValue(key);
        if (!HUnsafe.IsNullRef(ref valRef))
        {
            value = valRef;
            return true;
        }

        value = default;
        return false;
    }


    public bool TryAdd(TKey key, TValue value) =>
        TryInsert(key, value, InsertionBehavior.None);


    public bool Remove(TKey key)
    {
        // The overload Remove(TKey key, out TValue value) is a copy of this method with one additional
        // statement to copy the value for entry being removed into the output parameter.
        // Code has been intentionally duplicated for performance reasons.
        ArgumentNullException.ThrowIfNull(key, nameof(key));
        if (IsZeroCapacity || Count == 0)
            goto ReturnFail;

        uint collisionCount = 0;
        uint hashCode = (uint)key.GetHashCode();
        ref int bucket = ref GetBucket(hashCode);
        Span<Entry> entries = _entries;
        int last = -1;
        int i = bucket - 1; // Value in buckets is 1-based
        while (i >= 0)
        {
            ref Entry entry = ref entries[i];

            if (entry.HashCode == hashCode && EqualityComparer<TKey>.Default.Equals(entry.Key, key))
            {
                if (last < 0)
                {
                    bucket = entry.Next + 1; // Value in buckets is 1-based
                }
                else
                {
                    entries[last].Next = entry.Next;
                }

                Debug.Assert((StartOfFreeList - _freeList) < 0, "shouldn't underflow because max hashtable length is MaxPrimeArrayLength = 0x7FEFFFFD(2146435069) _freelist underflow threshold 2147483646");
                entry.Next = StartOfFreeList - _freeList;

                if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>())
                {
                    entry.Key = default!;
                }

                if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
                {
                    entry.Value = default!;
                }

                _freeList = i;
                _freeCount++;
                return true;
            }

            last = i;
            i = entry.Next;

            collisionCount++;
            if (collisionCount > (uint)entries.Length)
            {
                // The chain of entries forms a loop; which means a concurrent update has happened.
                // Break out of the loop and throw, rather than looping forever.
                throw new InvalidOperationException("ConcurrentOperationsNotSupported");
            }
        }

        ReturnFail:
            return false;
    }


    public bool Remove(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        // The overload Remove(TKey key, out TValue value) is a copy of this method with one additional
        // statement to copy the value for entry being removed into the output parameter.
        // Code has been intentionally duplicated for performance reasons.
        ArgumentNullException.ThrowIfNull(key, nameof(key));
        if (IsZeroCapacity || Count == 0)
            goto ReturnFail;

        uint collisionCount = 0;
        uint hashCode = (uint)key.GetHashCode();
        ref int bucket = ref GetBucket(hashCode);
        Span<Entry> entries = _entries;
        int last = -1;
        int i = bucket - 1; // Value in buckets is 1-based
        while (i >= 0)
        {
            ref Entry entry = ref entries[i];

            if (entry.HashCode == hashCode && EqualityComparer<TKey>.Default.Equals(entry.Key, key))
            {
                if (last < 0)
                {
                    bucket = entry.Next + 1; // Value in buckets is 1-based
                }
                else
                {
                    entries[last].Next = entry.Next;
                }
                value = entry.Value;

                Debug.Assert((StartOfFreeList - _freeList) < 0, "shouldn't underflow because max hashtable length is MaxPrimeArrayLength = 0x7FEFFFFD(2146435069) _freelist underflow threshold 2147483646");
                entry.Next = StartOfFreeList - _freeList;

                if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>())
                {
                    entry.Key = default!;
                }

                if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
                {
                    entry.Value = default!;
                }

                _freeList = i;
                _freeCount++;
                return true;
            }

            last = i;
            i = entry.Next;

            collisionCount++;
            if (collisionCount > (uint)entries.Length)
            {
                // The chain of entries forms a loop; which means a concurrent update has happened.
                // Break out of the loop and throw, rather than looping forever.
                throw new InvalidOperationException("ConcurrentOperationsNotSupported");
            }
        }

        ReturnFail:
            value = default;
            return false;
    }

    #endregion Методы


    #region Методы - Системные

    internal ref TValue FindValue(TKey key)
    {
        ArgumentNullException.ThrowIfNull(key, nameof(key));

        if (IsZeroCapacity || _count == 0)
            goto ReturnNotFound;

        ref Entry entry = ref HUnsafe.NullRef<Entry>();

        uint hashCode = (uint)key.GetHashCode();
        int i = GetBucket(hashCode);

        Span<Entry> entries = _entries;
        uint collisionCount = 0;


        // ValueType: Devirtualize with EqualityComparer<TValue>.Default intrinsic
        i--; // Value in _buckets is 1-based; subtract 1 from i. We do it here so it fuses with the following conditional.
        do
        {
            // Should be a while loop https://github.com/dotnet/runtime/issues/9422
            // Test in if to drop range check for following array access
            if ((uint)i >= (uint)entries.Length)
            {
                goto ReturnNotFound;
            }

            entry = ref entries[i];
            if (entry.HashCode == hashCode && EqualityComparer<TKey>.Default.Equals(entry.Key, key))
            {
                goto ReturnFound;
            }

            i = entry.Next;

            collisionCount++;
        } while (collisionCount <= (uint)entries.Length);

        // The chain of entries forms a loop; which means a concurrent update has happened.
        // Break out of the loop and throw, rather than looping forever.
        throw new InvalidOperationException("ConcurrentOperationsNotSupported");


        ReturnFound:
        ref TValue value = ref entry.Value;
        Return:
        return ref value;
        ReturnNotFound:
        value = ref HUnsafe.NullRef<TValue>();
        goto Return;
    }


    private bool TryInsert(TKey key, TValue value, InsertionBehavior behavior)
    {
        ArgumentNullException.ThrowIfNull(key, nameof(key));
        if (IsZeroCapacity)
            return false;

        Span<Entry> entries = _entries;
        Debug.Assert(entries != null, "expected entries to be non-null");


        uint hashCode = (uint)key.GetHashCode();

        uint collisionCount = 0;
        ref int bucket = ref GetBucket(hashCode);
        int i = bucket - 1; // Value in _buckets is 1-based


        // ValueType: Devirtualize with EqualityComparer<TValue>.Default intrinsic
        while (true)
        {
            // Should be a while loop https://github.com/dotnet/runtime/issues/9422
            // Test uint in if rather than loop condition to drop range check for following array access
            if ((uint)i >= (uint)entries.Length)
            {
                break;
            }

            if (entries[i].HashCode == hashCode && EqualityComparer<TKey>.Default.Equals(entries[i].Key, key))
            {
                if (behavior == InsertionBehavior.OverwriteExisting)
                {
                    entries[i].Value = value;
                    return true;
                }

                if (behavior == InsertionBehavior.ThrowOnExisting)
                {
                    throw new ArgumentException($"AddingDuplicateWithKey '{key}'", nameof(key));
                }

                return false;
            }

            i = entries[i].Next;

            collisionCount++;
            if (collisionCount > (uint)entries.Length)
            {
                // The chain of entries forms a loop; which means a concurrent update has happened.
                // Break out of the loop and throw, rather than looping forever.
                throw new InvalidOperationException("ConcurrentOperationsNotSupported");
            }
        }


        int index;
        if (_freeCount > 0)
        {
            index = _freeList;
            Debug.Assert((StartOfFreeList - entries[_freeList].Next) >= -1, "shouldn't overflow because `next` cannot underflow");
            _freeList = StartOfFreeList - entries[_freeList].Next;
            _freeCount--;
        }
        else
        {
            return false;
        }

        ref Entry entry = ref entries[index];
        entry.HashCode = hashCode;
        entry.Next = bucket - 1; // Value in _buckets is 1-based
        entry.Key = key;
        entry.Value = value;
        bucket = index + 1; // Value in _buckets is 1-based

        return true;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref int GetBucket(uint hashCode)
    {
        Span<int> buckets = _buckets;
        return ref buckets[(int)(hashCode % (uint)buckets.Length)];
    }

    #endregion Методы - Системные
}