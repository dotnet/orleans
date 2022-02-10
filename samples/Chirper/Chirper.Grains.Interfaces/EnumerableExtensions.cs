namespace System.Collections.Generic;

/// <summary>
/// Helper extensions for enumerables.
/// </summary>
public static class EnumerableExtensions
{
    /// <summary>
    /// Short-hand fora regular foreach.
    /// </summary>
    /// <typeparam name="T">The type of the element.</typeparam>
    /// <param name="enumerable">The enumerable to iterate.</param>
    /// <param name="action">The action to apply for each element.</param>
    public static void ForEach<T>(this IEnumerable<T> enumerable, Action<T> action)
    {
        foreach (var item in enumerable)
        {
            action(item);
        }
    }
}
