namespace Orleans.Serialization
{
    public interface ICopyContext : ISerializerContext
    {
        /// <summary>
        /// Record an object-to-copy mapping into the current serialization context.
        /// Used for maintaining the .NET object graph during serialization operations.
        /// Used in generated code.
        /// </summary>
        /// <param name="original">Original object.</param>
        /// <param name="copy">Copy object that will be the serialized form of the original.</param>
        void RecordCopy(object original, object copy);

        object CheckObjectWhileCopying(object raw);
    }
}