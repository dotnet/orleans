using System.Runtime.Serialization;

namespace Orleans.Serialization
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Reflection;

    /// <summary>
    /// Methods for serializing instances of <see cref="Exception"/> and its subclasses.
    /// </summary>
    internal class ILBasedExceptionSerializer
    {
        private static readonly Type ExceptionType = typeof(Exception);

        /// <summary>
        /// The collection of serializers.
        /// </summary>
        private readonly ConcurrentDictionary<Type, SerializationManager.SerializerMethods> serializers =
            new ConcurrentDictionary<Type, SerializationManager.SerializerMethods>();

        /// <summary>
        /// The field filter used for generating serializers for subclasses of <see cref="Exception"/>.
        /// </summary>
        private readonly Func<FieldInfo, bool> exceptionFieldFilter;

        /// <summary>
        /// The serializer used as a fallback when the concrete exception type is unavailable.
        /// </summary>
        /// <remarks>
        /// This serializer operates on <see cref="RemoteNonDeserializableException"/> instances, however it 
        /// includes only fields from <see cref="Exception"/> and no sub-class fields.
        /// </remarks>
        private readonly SerializationManager.SerializerMethods fallbackBaseExceptionSerializer;
        
        /// <summary>
        /// The serializer generator.
        /// </summary>
        private readonly ILSerializerGenerator generator;

        private readonly TypeSerializer typeSerializer;

        public ILBasedExceptionSerializer(ILSerializerGenerator generator, TypeSerializer typeSerializer)
        {
            this.generator = generator;
            this.typeSerializer = typeSerializer;

            // Exceptions are a special type in .NET because of the way they are handled by the runtime.
            // Only certain fields can be safely serialized.
            this.exceptionFieldFilter = field =>
            {
                // Any field defined below Exception is acceptable.
                if (field.DeclaringType != ExceptionType) return true;

                // Certain fields from the Exception base class are acceptable.
                return field.FieldType == typeof(string) || field.FieldType == ExceptionType;
            };

            // When serializing the fallback type, only the fields declared on Exception are included.
            // Other fields are manually serialized.
            this.fallbackBaseExceptionSerializer = this.generator.GenerateSerializer(
                typeof(RemoteNonDeserializableException),
                field =>
                {
                    // Only serialize base-class fields.
                    if (field.DeclaringType != ExceptionType) return false;

                    // Certain fields from the Exception base class are acceptable.
                    return field.FieldType == typeof(string) || field.FieldType == ExceptionType;
                },
                fieldComparer: ExceptionFieldInfoComparer.Instance);

            // Ensure that the fallback serializer only ever has its base exception fields serialized.
            this.serializers[typeof(RemoteNonDeserializableException)] = this.fallbackBaseExceptionSerializer;
        }

        public void Serialize(object item, ISerializationContext outerContext, Type expectedType)
        {
            var outerWriter = outerContext.StreamWriter;
            
            var actualType = item.GetType();

            // To support loss-free serialization where possible, instances of the fallback exception type are serialized in a
            // semi-manual fashion.
            var fallbackException = item as RemoteNonDeserializableException;
            if (fallbackException != null)
            {
                this.ReserializeFallback(fallbackException, outerContext);
                return;
            }
            
            // Write the concrete type directly.
            this.typeSerializer.WriteNamedType(actualType, outerWriter);

            var innerContext = new SerializationContext
            {
                StreamWriter = new BinaryTokenStreamWriter()
            };

            // Serialize the exception itself.
            var methods = this.GetSerializerMethods(actualType);
            methods.Serialize(item, innerContext, null);

            // Write the serialized exception to the output stream.
            outerContext.StreamWriter.Write(innerContext.StreamWriter.CurrentOffset);
            outerContext.StreamWriter.Write(innerContext.StreamWriter.ToBytes());
        }

        public object Deserialize(Type expectedType, IDeserializationContext outerContext)
        {
            var outerReader = outerContext.StreamReader;

            var typeKey = TypeSerializer.ReadTypeKey(outerReader);
            
            // Read the serialized payload.
            var length = outerReader.ReadInt();
            var innerBytes = outerReader.ReadBytes(length);
            
            var innerContext = new DeserializationContext
            {
                StreamReader = new BinaryTokenStreamReader(innerBytes)
            };
            
            object result;

            // If the concrete type is available and the exception is valid for reconstruction,
            // reconstruct the original exception type.
            var actualType = this.typeSerializer.GetTypeFromTypeKey(typeKey, throwOnError: false);
            if (actualType != null)
            {
                // Deserialize into the concrete type.
                var methods = this.GetSerializerMethods(actualType);
                result = methods.Deserialize(null, innerContext);
            }
            else
            {
                // Since the concrete type is unavailable, deserialize into the fallback type.

                // Read the Exception fields.
                var exception = (RemoteNonDeserializableException)this.fallbackBaseExceptionSerializer.Deserialize(null, innerContext);
                exception.OriginalTypeName = typeKey.GetTypeName();

                // If there is additional data, store it for later serialization.
                var additionalDataLength = length - innerContext.StreamReader.CurrentPosition;
                if (additionalDataLength > 0)
                {
                    exception.AdditionalData = innerContext.StreamReader.ReadBytes(additionalDataLength);
                }

                // If a particular type is expected, but the actual type is not available, throw an exception to avoid 
                if (expectedType != null && !expectedType.IsAssignableFrom(typeof(RemoteNonDeserializableException)))
                {
                    throw new SerializationException(
                        $"Unable to deserialize exception of unavailable type {exception.OriginalTypeName} into expected type {expectedType}. " +
                        $" See {nameof(Exception.InnerException)} for recovered information.",
                        exception);
                }

                result = exception;
            }
            
            return result;
        }

        /// <summary>
        /// Returns a copy of the provided instance.
        /// </summary>
        /// <param name="original">The object to copy.</param>
        /// <param name="context">The copy context.</param>
        /// <returns>A copy of the provided instance.</returns>
        public object DeepCopy(object original, ICopyContext context)
        {
            return original;
        }

        private void ReserializeFallback(RemoteNonDeserializableException fallbackException, ISerializationContext outerContext)
        {
            var outerWriter = outerContext.StreamWriter;

            // Write the type name directly.
            var key = new TypeSerializer.TypeKey(fallbackException.OriginalTypeName);
            TypeSerializer.WriteTypeKey(key, outerWriter);
            
            // Serialize the only accepted fields from the base Exception class.
            var innerContext = new SerializationContext
            {
                StreamWriter = new BinaryTokenStreamWriter()
            };
            this.fallbackBaseExceptionSerializer.Serialize(fallbackException, innerContext, null);

            // Write the length of the serialized exception, then write the serialized bytes.
            var additionalDataLength = fallbackException.AdditionalData?.Length ?? 0;
            outerWriter.Write(innerContext.StreamWriter.CurrentOffset + additionalDataLength);
            outerWriter.Write(innerContext.StreamWriter.ToBytes());
            
            if (additionalDataLength > 0)
            {
                outerWriter.Write(fallbackException.AdditionalData);
            }
        }

        private SerializationManager.SerializerMethods GetSerializerMethods(Type actualType)
        {
            SerializationManager.SerializerMethods methods;
            if (!this.serializers.TryGetValue(actualType, out methods))
            {
                methods = this.generator.GenerateSerializer(
                    actualType,
                    this.exceptionFieldFilter,
                    fieldComparer: ExceptionFieldInfoComparer.Instance);
                this.serializers.TryAdd(actualType, methods);
            }

            return methods;
        }

        /// <summary>
        /// Field comparer which sorts fields on the Exception class higher than fields on sub classes.
        /// </summary>
        private class ExceptionFieldInfoComparer : IComparer<FieldInfo>
        {
            /// <summary>
            /// Gets the singleton instance of this class.
            /// </summary>
            public static ExceptionFieldInfoComparer Instance { get; } = new ExceptionFieldInfoComparer();

            public int Compare(FieldInfo left, FieldInfo right)
            {
                var l = left.DeclaringType == ExceptionType ? 1 : 0;
                var r = right.DeclaringType == ExceptionType ? 1 : 0;

                // First compare based on whether or not the field is from the Exception base class.
                var compareBaseClass = r - l;
                if (compareBaseClass != 0) return compareBaseClass;

                // Secondarily compare the field names.
                return string.Compare(left.Name, right.Name, StringComparison.Ordinal);
            }
        }
    }
}