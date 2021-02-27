namespace Orleans.Serialization.Serializers
{
    public interface IValueSerializerProvider
    {
        IValueSerializer<TField> GetValueSerializer<TField>() where TField : struct;
    }
}