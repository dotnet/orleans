namespace Orleans
{
    /// <summary>
    /// Creates an instance of <typeparamref name="TInstance"/>.
    /// </summary>
    /// <typeparam name="TInstance"></typeparam>
    /// <returns></returns>
    public delegate TInstance Factory<out TInstance>();

    /// <summary>
    /// Creates an instance of <typeparamref name="TInstance"/>.
    /// </summary>
    /// <typeparam name="TInstance"></typeparam>
    /// <typeparam name="TParam1"></typeparam>
    /// <returns></returns>
    public delegate TInstance Factory<in TParam1, out TInstance>(TParam1 param1);

    /// <summary>
    /// Creates an instance of <typeparamref name="TInstance"/>.
    /// </summary>
    /// <typeparam name="TInstance"></typeparam>
    /// <typeparam name="TParam1"></typeparam>
    /// <typeparam name="TParam2"></typeparam>
    /// <returns></returns>
    public delegate TInstance Factory<in TParam1, in TParam2, out TInstance>(TParam1 param1, TParam2 param2);

    /// <summary>
    /// Creates an instance of <typeparamref name="TInstance"/>.
    /// </summary>
    /// <typeparam name="TInstance"></typeparam>
    /// <typeparam name="TParam1"></typeparam>
    /// <typeparam name="TParam2"></typeparam>
    /// <typeparam name="TParam3"></typeparam>
    /// <returns></returns>
    public delegate TInstance Factory<in TParam1, in TParam2, in TParam3, out TInstance>(TParam1 param1, TParam2 param2, TParam3 param3);
}
