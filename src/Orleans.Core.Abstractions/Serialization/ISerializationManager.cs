using System;

namespace Orleans.Serialization
{
    public interface ISerializationManager
    {
        /// <summary>
        /// Deep copy the specified object, using DeepCopier functions previously registered for this type.
        /// </summary>
        /// <param name="original">The input data to be deep copied.</param>
        /// <returns>Deep copied clone of the original input object.</returns>
        object DeepCopy(object original);

        /// <summary>
        /// Serialize the specified object, using Serializer functions previously registered for this type.
        /// </summary>
        /// <param name="raw">The input data to be serialized.</param>
        /// <param name="stream">The output stream to write to.</param>
        void Serialize(object raw, IBinaryTokenStreamWriter stream);

        /// <summary>
        /// Serialize data into byte[].
        /// </summary>
        /// <param name="raw">Input data.</param>
        /// <returns>Object of the required Type, rehydrated from the input stream.</returns>
        byte[] SerializeToByteArray(object raw);

        /// <summary>
        /// Deserialize the next object from the input binary stream.
        /// </summary>
        /// <param name="stream">Input stream.</param>
        /// <returns>Object of the required Type, rehydrated from the input stream.</returns>
        object Deserialize(IBinaryTokenStreamReader stream);

        /// <summary>
        /// Deserialize the next object from the input binary stream.
        /// </summary>
        /// <typeparam name="T">Type to return.</typeparam>
        /// <param name="stream">Input stream.</param>
        /// <returns>Object of the required Type, rehydrated from the input stream.</returns>
        T Deserialize<T>(IBinaryTokenStreamReader stream);

        /// <summary>
        /// Deserialize the next object from the input binary stream.
        /// </summary>
        /// <param name="t">Type to return.</param>
        /// <param name="stream">Input stream.</param>
        /// <returns>Object of the required Type, rehydrated from the input stream.</returns>
        object Deserialize(Type t, IBinaryTokenStreamReader stream);

        /// <summary>
        /// Deserialize data from the specified byte[] and rehydrate backi into objects.
        /// </summary>
        /// <typeparam name="T">Type of data to be returned.</typeparam>
        /// <param name="data">Input data.</param>
        /// <returns>Object of the required Type, rehydrated from the input stream.</returns>
        T DeserializeFromByteArray<T>(byte[] data);
    }
}