using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Orleans;
using TestExtensions;

namespace UnitTests.Serialization
{
    using Orleans.Serialization;

    using Xunit;

    [TestCategory("BVT"), TestCategory("Serialization")]
    public class ILBasedExceptionSerializerTests
    {
        private readonly ILSerializerGenerator serializerGenerator = new ILSerializerGenerator();
        private readonly SerializationTestEnvironment environment;

        public ILBasedExceptionSerializerTests()
        {
            this.environment = SerializationTestEnvironment.Initialize(null, typeof(ILBasedSerializer).GetTypeInfo());
        }

        /// <summary>
        /// Tests that <see cref="ILBasedExceptionSerializer"/> supports distinct field selection for serialization
        /// versus copy operations.
        /// </summary>
        [Fact]
        public void ExceptionSerializer_SimpleException()
        {
            // Throw an exception so that is has a stack trace.
            var expected = GetNewException();

            var writer = new SerializationContext(this.environment.SerializationManager)
            {
                StreamWriter = new BinaryTokenStreamWriter()
            };

            // Deep copies should be reference-equal.
            Assert.Equal(expected, SerializationManager.DeepCopyInner(expected, new SerializationContext(this.environment.SerializationManager)), ReferenceEqualsComparer.Instance);

            this.environment.SerializationManager.Serialize(expected, writer.StreamWriter);
            var reader = new DeserializationContext(this.environment.SerializationManager)
            {
                StreamReader = new BinaryTokenStreamReader(writer.StreamWriter.ToByteArray())
            };

            var actual = (ILExceptionSerializerTestException)this.environment.SerializationManager.Deserialize(null, reader.StreamReader);
            Assert.Equal(expected.BaseField.Value, actual.BaseField.Value, StringComparer.Ordinal);
            Assert.Equal(expected.SubClassField, actual.SubClassField, StringComparer.Ordinal);
            Assert.Equal(expected.OtherField.Value, actual.OtherField.Value, StringComparer.Ordinal);

            // Check for referential equality in the two fields which happened to be reference-equals.
            Assert.Equal(actual.BaseField, actual.OtherField, ReferenceEqualsComparer.Instance);
        }

        private static ILExceptionSerializerTestException GetNewException()
        {
            ILExceptionSerializerTestException expected;
            try
            {
                var baseField = new SomeFunObject
                {
                    Value = Guid.NewGuid().ToString()
                };
                var res = new ILExceptionSerializerTestException
                {
                    BaseField = baseField,
                    SubClassField = Guid.NewGuid().ToString(),
                    OtherField = baseField,
                };
                throw res;
            }
            catch (ILExceptionSerializerTestException exception)
            {
                expected = exception;
            }
            return expected;
        }

        /// <summary>
        /// Tests that <see cref="ILBasedExceptionSerializer"/> supports distinct field selection for serialization
        /// versus copy operations.
        /// </summary>
        [Fact]
        public void ExceptionSerializer_UnknownException()
        {
            var expected = GetNewException();

            var knowsException = new ILBasedExceptionSerializer(this.serializerGenerator, new TypeSerializer());
            
            var writer = new SerializationContext(this.environment.SerializationManager)
            {
                StreamWriter = new BinaryTokenStreamWriter()
            };
            knowsException.Serialize(expected, writer, null);

            // Deep copies should be reference-equal.
            var copyContext = new SerializationContext(this.environment.SerializationManager);
            Assert.Equal(expected, knowsException.DeepCopy(expected, copyContext), ReferenceEqualsComparer.Instance);

            // Create a deserializer which doesn't know about the expected exception type.
            var reader = new DeserializationContext(this.environment.SerializationManager)
            {
                StreamReader = new BinaryTokenStreamReader(writer.StreamWriter.ToByteArray())
            };

            // Ensure that the deserialized object has the fallback type.
            var doesNotKnowException = new ILBasedExceptionSerializer(this.serializerGenerator, new TestTypeSerializer());
            var untypedActual = doesNotKnowException.Deserialize(null, reader);
            Assert.IsType<RemoteNonDeserializableException>(untypedActual);

            // Ensure that the original type name is preserved correctly.
            var actualDeserialized = (RemoteNonDeserializableException) untypedActual;
            Assert.Equal(typeof(ILExceptionSerializerTestException).AssemblyQualifiedName, actualDeserialized.OriginalTypeName);

            // Re-serialize the deserialized object using the serializer which does not have access to the original type.
            writer = new SerializationContext(this.environment.SerializationManager)
            {
                StreamWriter = new BinaryTokenStreamWriter()
            };
            doesNotKnowException.Serialize(untypedActual, writer, null);

            reader = new DeserializationContext(this.environment.SerializationManager)
            {
                StreamReader = new BinaryTokenStreamReader(writer.StreamWriter.ToByteArray())
            };

            // Deserialize the round-tripped object and verify that it has the original type and all properties are
            // correctly.
            untypedActual = knowsException.Deserialize(null, reader);
            Assert.IsType<ILExceptionSerializerTestException>(untypedActual);

            var actual = (ILExceptionSerializerTestException) untypedActual;
            Assert.Equal(expected.BaseField.Value, actual.BaseField.Value, StringComparer.Ordinal);
            Assert.Equal(expected.SubClassField, actual.SubClassField, StringComparer.Ordinal);
            Assert.Equal(expected.OtherField.Value, actual.OtherField.Value, StringComparer.Ordinal);

            // Check for referential equality in the two fields which happened to be reference-equals.
            Assert.Equal(actual.BaseField, actual.OtherField, ReferenceEqualsComparer.Instance);
        }

        private class SomeFunObject
        {
            public string Value { get; set; }
        }

        private class BaseException : Exception
        {
            public SomeFunObject BaseField { get; set; }
        }

        private class ILExceptionSerializerTestException : BaseException
        {
            public string SubClassField { get; set; }
            public SomeFunObject OtherField { get; set; }
        }

        private class TestTypeSerializer : TypeSerializer
        {
            internal override Type GetTypeFromName(string assemblyQualifiedTypeName, bool throwOnError)
            {
                if (throwOnError) throw new TypeLoadException($"Type {assemblyQualifiedTypeName} could not be loaded");
                return null;
            }
        }
    }
}