using System;
using System.Collections.Generic;
using System.Numerics;

namespace GatherBuddy.Utility;

public static class CollectionExtensions
{
    public static IEnumerable<(T Value, int Index)> WithIndex<T>(this IEnumerable<T> list)
    {
        var index = 0;
        foreach (var item in list)
        {
            yield return (item, index++);
        }
    }

    public static int IndexOf<T>(this IEnumerable<T> list, Predicate<T> predicate)
    {
        var index = 0;
        foreach (var item in list)
        {
            if (predicate(item))
            {
                return index;
            }

            ++index;
        }

        return -1;
    }

    public static void StableSort<T>(this IList<T> list, Comparison<T> comparer)
    {
        var indexed = new (T Value, int Index)[list.Count];
        for (var i = 0; i < indexed.Length; ++i)
        {
            indexed[i] = (list[i], i);
        }

        Array.Sort(indexed, (a, b) =>
        {
            var ret = comparer(a.Value, b.Value);
            return ret != 0 ? ret : a.Index.CompareTo(b.Index);
        });
        for (var i = 0; i < indexed.Length; ++i)
        {
            list[i] = indexed[i].Value;
        }
    }

    public static Vector3 Average(this IEnumerable<Vector3> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var sum = Vector3.Zero;
        var count = 0;
        foreach (var v in source)
        {
            sum += v;
            ++count;
        }

        if (count == 0)
        {
            throw new InvalidOperationException("Sequence contains no elements");
        }

        return sum / count;
    }
}
