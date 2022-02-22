namespace Orleans.Serialization.Serializers
{
    /// <summary>
    /// For codecs which have been wrapped in another type, provides access to the codec.
    /// </summary>
    internal interface IWrappedCodec
    {
        /// <summary>
        /// Gets the inner codec.
        /// </summary>
        /// <value>The inner codec.</value>
        object Inner { get; }
    }

    /// <summary>
    /// Holds a reference to a service.
    /// </summary>
    /// <typeparam name="T">The service type.</typeparam>
    internal interface IServiceHolder<T>
    {
        /// <summary>
        /// Gets the service.
        /// </summary>
        /// <value>The service.</value>
        T Value { get; }
    }
}