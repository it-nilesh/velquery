using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace VelQuery.Execution;

/// <summary>
/// Accumulates buffered rows in <see cref="ArrayPool{T}"/> storage and produces an exact-size array, so a query
/// allocates only its result array instead of every intermediate <see cref="List{T}"/> growth step.
/// </summary>
internal struct RowBuffer<T>
{
    private T[]? _items;
    private int _count;

    public void Add(T item)
    {
        if (_items is null)
        {
            _items = ArrayPool<T>.Shared.Rent(16);
        }
        else if (_count == _items.Length)
        {
            var larger = ArrayPool<T>.Shared.Rent(_items.Length * 2);
            Array.Copy(_items, larger, _count);
            Return(_items, _count);
            _items = larger;
        }

        _items[_count++] = item;
    }

    /// <summary>Copies the rows to an exact-size array and returns pooled storage.</summary>
    public IReadOnlyList<T> ToResult()
    {
        if (_items is null || _count == 0)
        {
            Dispose();
            return Array.Empty<T>();
        }

        var result = new T[_count];
        Array.Copy(_items, result, _count);
        Dispose();
        return result;
    }

    /// <summary>Returns pooled storage; safe to call more than once.</summary>
    public void Dispose()
    {
        if (_items is not null)
        {
            Return(_items, _count);
            _items = null;
            _count = 0;
        }
    }

    private static void Return(T[] items, int count)
    {
        // Clear references so pooled arrays don't keep result rows alive.
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            Array.Clear(items, 0, count);
        }

        ArrayPool<T>.Shared.Return(items);
    }
}
