namespace Orleans.Serialization.Serializers
{
    public interface IBaseCodecProvider
    {
        IBaseCodec<TField> GetBaseCodec<TField>() where TField : class;
    }
}