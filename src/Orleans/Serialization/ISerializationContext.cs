namespace Orleans.Serialization
{
    public interface ISerializationContext : ISerializerContext
    {
        BinaryTokenStreamWriter StreamWriter { get; }

        void RecordObject(object original);

        int CheckObjectWhileSerializing(object raw);
    }
}