namespace Orleans.Serialization.Serializers
{
    internal interface IWrappedCodec
    {
        object Inner { get; }
    }

    internal interface IServiceHolder<T>
    {
        T Value { get; }
    }
}