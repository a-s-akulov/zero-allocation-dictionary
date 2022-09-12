// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using Server.Common.Unsafe.Collections.Generic;

namespace Server.Common.Unsafe.Extensions;


public static class IEnumarableExtensions
{
    /// <summary>
    /// It's better to pass <paramref name="sourceLength"/> due to perfomance reasons
    /// </summary>
    /// <typeparam name="TElement"></typeparam>
    /// <param name="source"></param>
    /// <param name="filter"></param>
    /// <param name="sourceLength"></param>
    /// <returns>
    /// The most common element with it's count<br/>
    /// Or <see langword="null"/> if <paramref name="source"/> was empty
    /// </returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static KeyValuePair<int, TElement>? MaxCount<TElement>(
        this IEnumerable<TElement> source,
        Func<TElement, bool> filter = null,
        Func<TElement, TElement, TElement> equalsResolver = null,
        int? sourceLength = null
    ) => MaxCount(source, element => element, filter: filter, equalsResolver: equalsResolver);
    /// <inheritdoc cref="MaxCount"/>
    public static KeyValuePair<int, TElement>? MaxCount<TElement, TKey>(
                this IEnumerable<TElement> source,
                Func<TElement, TKey> keySelector,
                Func<TElement, bool> filter = null,
                Func<TElement, TElement, TElement> equalsResolver = null,
                int? sourceLength = null
            )
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (keySelector == null) throw new ArgumentNullException(nameof(keySelector));

        sourceLength ??= source.Count();
        if (sourceLength == 0)
            return null;

        TElement maxElement = default;
        var maxElementCount = 0;

        if (sourceLength <= byte.MaxValue)
        {
            // HIGH-PERFORMANCE
            var dict = ValueDictionary<int, int>.ZeroCapacity;

            foreach (var element in source)
            {
                if (filter != null && !filter(element))
                    continue;

                if (dict.IsZeroCapacity)
                    dict = new ValueDictionary<int, int>((byte)sourceLength);

                int count;
                var key = keySelector(element);
                var keyHashCode = key.GetHashCode();

                if (dict.ContainsKey(keyHashCode))
                {
                    count = ++dict[keyHashCode];
                }
                else
                {
                    count = 1;
                    dict[keyHashCode] = count;
                }

                if (count == maxElementCount)
                {
                    if (equalsResolver != null)
                        maxElement = equalsResolver(maxElement, element);
                }
                else if (count > maxElementCount)
                {
                    maxElement = element;
                    maxElementCount = count;
                }
            }
        }
        else
        {
            // DEFAULT-PERFORMANCE
            Dictionary<TKey, int> dict = null;

            foreach (var element in source)
            {
                if (filter != null && !filter(element))
                    continue;

                dict ??= new Dictionary<TKey, int>();

                int count;
                var key = keySelector(element);
                if (dict.ContainsKey(key))
                {
                    count = ++dict[key];
                }
                else
                {
                    count = 1;
                    dict[key] = count;
                }

                if (count == maxElementCount)
                {
                    if (equalsResolver != null)
                        maxElement = equalsResolver(maxElement, element);
                }
                else if (count > maxElementCount)
                {
                    maxElement = element;
                    maxElementCount = count;
                }
            }
        }

        return new KeyValuePair<int, TElement>(maxElementCount, maxElement);
    }
}