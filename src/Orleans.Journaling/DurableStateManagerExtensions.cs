namespace Orleans.Journaling;

/// <summary>
/// Provides convenient access to the built-in durable state contracts.
/// </summary>
public static class DurableStateManagerExtensions
{
    /// <summary>Gets or creates a named durable dictionary.</summary>
    /// <typeparam name="TKey">The dictionary key type.</typeparam>
    /// <typeparam name="TValue">The dictionary value type.</typeparam>
    /// <param name="manager">The owning manager.</param>
    /// <param name="name">The stable state name.</param>
    /// <returns>The named dictionary instance.</returns>
    public static IDurableDictionary<TKey, TValue> GetOrAddDictionary<TKey, TValue>(this IDurableStateManager manager, string name)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(manager);
        return manager.GetOrAddState<IDurableDictionary<TKey, TValue>>(name);
    }

    /// <summary>Gets or creates a named durable list.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="manager">The owning manager.</param>
    /// <param name="name">The stable state name.</param>
    /// <returns>The named list instance.</returns>
    public static IDurableList<T> GetOrAddList<T>(this IDurableStateManager manager, string name)
    {
        ArgumentNullException.ThrowIfNull(manager);
        return manager.GetOrAddState<IDurableList<T>>(name);
    }

    /// <summary>Gets or creates a named durable queue.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="manager">The owning manager.</param>
    /// <param name="name">The stable state name.</param>
    /// <returns>The named queue instance.</returns>
    public static IDurableQueue<T> GetOrAddQueue<T>(this IDurableStateManager manager, string name)
    {
        ArgumentNullException.ThrowIfNull(manager);
        return manager.GetOrAddState<IDurableQueue<T>>(name);
    }

    /// <summary>Gets or creates a named durable set.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="manager">The owning manager.</param>
    /// <param name="name">The stable state name.</param>
    /// <returns>The named set instance.</returns>
    public static IDurableSet<T> GetOrAddSet<T>(this IDurableStateManager manager, string name)
    {
        ArgumentNullException.ThrowIfNull(manager);
        return manager.GetOrAddState<IDurableSet<T>>(name);
    }

    /// <summary>Gets or creates a named durable value.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="manager">The owning manager.</param>
    /// <param name="name">The stable state name.</param>
    /// <returns>The named value instance.</returns>
    public static IDurableValue<T> GetOrAddValue<T>(this IDurableStateManager manager, string name)
    {
        ArgumentNullException.ThrowIfNull(manager);
        return manager.GetOrAddState<IDurableValue<T>>(name);
    }

    /// <summary>Gets or creates a named durable task completion source.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="manager">The owning manager.</param>
    /// <param name="name">The stable state name.</param>
    /// <returns>The named task completion source instance.</returns>
    public static IDurableTaskCompletionSource<T> GetOrAddTaskCompletionSource<T>(this IDurableStateManager manager, string name)
    {
        ArgumentNullException.ThrowIfNull(manager);
        return manager.GetOrAddState<IDurableTaskCompletionSource<T>>(name);
    }

    /// <summary>Gets or creates named journal-backed persistent state.</summary>
    /// <typeparam name="T">The persisted value type.</typeparam>
    /// <param name="manager">The owning manager.</param>
    /// <param name="name">The stable state name.</param>
    /// <returns>The named persistent state instance.</returns>
    public static IPersistentState<T> GetOrAddPersistentState<T>(this IDurableStateManager manager, string name)
    {
        ArgumentNullException.ThrowIfNull(manager);
        return manager.GetOrAddState<IPersistentState<T>>(name);
    }
}
