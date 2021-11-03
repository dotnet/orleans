using System;
using System.Runtime.Serialization;
using Orleans;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Utilities;
using TestExtensions;
using Xunit;

namespace UnitTests.Serialization
{
    [TestCategory("BVT"), TestCategory("Serialization")]
    public class ILBasedExceptionSerializerTests
    {
        private readonly ILSerializerGenerator serializerGenerator = new ILSerializerGenerator();
        private readonly SerializationTestEnvironment environment;

        public ILBasedExceptionSerializerTests()
        {
            this.environment = SerializationTestEnvironment.Initialize(null, typeof(ILBasedSerializer));
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
            this.TestExceptionSerialization(expected);
        }

        private ILExceptionSerializerTestException TestExceptionSerialization(ILExceptionSerializerTestException expected)
        {
            var writer = new BinaryTokenStreamWriter();

            // Deep copies should be reference-equal.
            Assert.Equal(
                expected,
                SerializationManager.DeepCopyInner(expected, new SerializationContext(this.environment.SerializationManager)),
                ReferenceEqualsComparer.Instance);

            this.environment.SerializationManager.Serialize(expected, writer);
            var reader = new DeserializationContext(this.environment.SerializationManager)
            {
                StreamReader = new BinaryTokenStreamReader(writer.ToByteArray())
            };

            var actual = (ILExceptionSerializerTestException) this.environment.SerializationManager.Deserialize(null, reader.StreamReader);
            Assert.Equal(expected.BaseField.Value, actual.BaseField.Value, StringComparer.Ordinal);
            Assert.Equal(expected.SubClassField, actual.SubClassField, StringComparer.Ordinal);
            Assert.Equal(expected.OtherField.Value, actual.OtherField.Value, StringComparer.Ordinal);

            return actual;
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

            var knowsException = new ILBasedExceptionSerializer(this.serializerGenerator, new TypeSerializer(new CachedTypeResolver()));

            var writer = new BinaryTokenStreamWriter();
            var context = new SerializationContext(this.environment.SerializationManager)
            {
                StreamWriter = writer
            };
            knowsException.Serialize(expected, context, null);

            // Deep copies should be reference-equal.
            var copyContext = new SerializationContext(this.environment.SerializationManager);
            Assert.Equal(expected, knowsException.DeepCopy(expected, copyContext), ReferenceEqualsComparer.Instance);

            // Create a deserializer which doesn't know about the expected exception type.
            var reader = new DeserializationContext(this.environment.SerializationManager)
            {
                StreamReader = new BinaryTokenStreamReader(writer.ToByteArray())
            };

            // Ensure that the deserialized object has the fallback type.
            var doesNotKnowException = new ILBasedExceptionSerializer(this.serializerGenerator, new TestTypeSerializer(new CachedTypeResolver()));
            var untypedActual = doesNotKnowException.Deserialize(null, reader);
            Assert.IsType<RemoteNonDeserializableException>(untypedActual);

            // Ensure that the original type name is preserved correctly.
            var actualDeserialized = (RemoteNonDeserializableException) untypedActual;
            Assert.Equal(RuntimeTypeNameFormatter.Format(typeof(ILExceptionSerializerTestException)), actualDeserialized.OriginalTypeName);

            // Re-serialize the deserialized object using the serializer which does not have access to the original type.
            writer = new BinaryTokenStreamWriter();
            context = new SerializationContext(this.environment.SerializationManager)
            {
                StreamWriter = writer
            };
            doesNotKnowException.Serialize(untypedActual, context, null);

            reader = new DeserializationContext(this.environment.SerializationManager)
            {
                StreamReader = new BinaryTokenStreamReader(writer.ToByteArray())
            };

            // Deserialize the round-tripped object and verify that it has the original type and all properties are
            // correctly.
            untypedActual = knowsException.Deserialize(null, reader);
            Assert.IsType<ILExceptionSerializerTestException>(untypedActual);

            var actual = (ILExceptionSerializerTestException) untypedActual;
            Assert.Equal(expected.BaseField.Value, actual.BaseField.Value, StringComparer.Ordinal);
            Assert.Equal(expected.SubClassField, actual.SubClassField, StringComparer.Ordinal);
            Assert.Equal(expected.OtherField.Value, actual.OtherField.Value, StringComparer.Ordinal);
        }

        private class Outer
        {
            public SomeFunObject SomeFunObject { get; set; }
            public ILExceptionSerializerTestException Object { get; set; }
        }

        private class SomeFunObject
        {
            public string Value { get; set; }
        }

        private class BaseException : Exception
        {
            public SomeFunObject BaseField { get; set; }
            public BaseException() { }

            protected BaseException(SerializationInfo info, StreamingContext context) : base(info, context)
            {
                BaseField = (SomeFunObject)info.GetValue(nameof(BaseField), typeof(SomeFunObject));
            }

            public override void GetObjectData(SerializationInfo info, StreamingContext context)
            {
                base.GetObjectData(info, context);
                info.AddValue(nameof(BaseField), BaseField, typeof(SomeFunObject));
            }
        }

        [Serializable]
        private class ILExceptionSerializerTestException : BaseException
        {
            public string SubClassField { get; set; }
            public SomeFunObject OtherField { get; set; }
            public object SomeObject { get; set; }

            public ILExceptionSerializerTestException() { }

            protected ILExceptionSerializerTestException(SerializationInfo info, StreamingContext context) : base(info, context)
            {
                OtherField = (SomeFunObject)info.GetValue(nameof(OtherField), typeof(SomeFunObject));
                SubClassField = info.GetString(nameof(SubClassField));
                SomeObject = info.GetValue(nameof(SomeObject), typeof(object));
            }

            public override void GetObjectData(SerializationInfo info, StreamingContext context)
            {
                base.GetObjectData(info, context);
                info.AddValue(nameof(SubClassField), SubClassField, typeof(string));
                info.AddValue(nameof(OtherField), OtherField, typeof(SomeFunObject));
                info.AddValue(nameof(SomeObject), SomeObject, typeof(object));
            }
        }

        private class TestTypeSerializer : TypeSerializer
        {
            internal override Type GetTypeFromName(string assemblyQualifiedTypeName, bool throwOnError)
            {
                if (throwOnError) throw new TypeLoadException($"Type {assemblyQualifiedTypeName} could not be loaded");
                return null;
            }

            public TestTypeSerializer(ITypeResolver typeResolver) : base(typeResolver)
            {
            }
        }
    }
}