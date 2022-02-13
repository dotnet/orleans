namespace Orleans
{
    /// <summary>
    /// Creates an instance of <typeparamref name="TInstance"/>.
    /// </summary>
    /// <typeparam name="TInstance"></typeparam>
    /// <returns>The instance.</returns>
    public delegate TInstance Factory<out TInstance>();

    /// <summary>
    /// Creates an instance of <typeparamref name="TInstance"/>.
    /// </summary>
    /// <typeparam name="TInstance">The instance type.</typeparam>
    /// <typeparam name="TParam1">The parameter type.</typeparam>
    /// <returns>The instance.</returns>
    public delegate TInstance Factory<in TParam1, out TInstance>(TParam1 param1);

    /// <summary>
    /// Creates an instance of <typeparamref name="TInstance"/>.
    /// </summary>
    /// <typeparam name="TInstance">The instance type.</typeparam>
    /// <typeparam name="TParam1">The first parameter type.</typeparam>
    /// <typeparam name="TParam2">The second parameter type.</typeparam>
    /// <returns>The instance.</returns>
    public delegate TInstance Factory<in TParam1, in TParam2, out TInstance>(TParam1 param1, TParam2 param2);

    /// <summary>
    /// Creates an instance of <typeparamref name="TInstance"/>.
    /// </summary>
    /// <typeparam name="TInstance">The instance type.</typeparam>
    /// <typeparam name="TParam1">The first parameter type.</typeparam>
    /// <typeparam name="TParam2">The second parameter type.</typeparam>
    /// <typeparam name="TParam3">The third parameter type.</typeparam>
    /// <returns>The instance.</returns>
    public delegate TInstance Factory<in TParam1, in TParam2, in TParam3, out TInstance>(TParam1 param1, TParam2 param2, TParam3 param3);
}
