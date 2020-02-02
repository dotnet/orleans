using Orleans;
using Orleans.CodeGeneration;
using Orleans.Serialization;
using System;
using TestExtensions;
using Xunit;

namespace Tester.SerializationTests
{
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class NonStaticSerializerTests
    {
        private readonly TestEnvironmentFixture fixture;

        public NonStaticSerializerTests(TestEnvironmentFixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void ConstructorIsCalled()
        {
            SimplePocoClassSerializer.CallCounter = 0;
            var input = new SimplePocoClass { A = 30 };
            var output = fixture.SerializationManager.RoundTripSerializationForTesting(input);
            Assert.Equal(2, SimplePocoClassSerializer.CallCounter);
            Assert.Equal(input.A, output.A);
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void StaticMethodStillRegistered()
        {
            SimplePocoClassSerializer.CallCounter = 0;
            var input = new SimplePocoClass { A = 30 };
            var output = (SimplePocoClass) fixture.SerializationManager.DeepCopy(input);
            Assert.Equal(1, SimplePocoClassSerializer.CallCounter);
            Assert.Equal(input.A, output.A);
        }
    }

    public class SimplePocoClass
    {
        public int A { get; set; }
    }

    [Serializer(typeof(SimplePocoClass))]
    public class SimplePocoClassSerializer
    {
        private IGrainFactory grainFactory;

        public static int CallCounter { get; set; }

        public SimplePocoClassSerializer(IGrainFactory grainFactory)
        {
            if (grainFactory == null) throw new ArgumentNullException(nameof(grainFactory));
            this.grainFactory = grainFactory;
        }

        [CopierMethod]
        public static object DeepCopier(object original, ICopyContext context)
        {
            CallCounter++;
            return new SimplePocoClass { A = ((SimplePocoClass)original).A };
        }

        [SerializerMethod]
        public void Serialize(object obj, ISerializationContext context, Type expected)
        {
            AssertConstructorHasBeenCalled();

            CallCounter++;
            context.GetSerializationManager().Serialize(((SimplePocoClass)obj).A, context.StreamWriter);
        }

        [DeserializerMethod]
        public object Deserialize(Type expected, IDeserializationContext context)
        {
            AssertConstructorHasBeenCalled();

            CallCounter++;
            var a = (int) context.GetSerializationManager().Deserialize(typeof(int), context.StreamReader);
            return new SimplePocoClass { A = a };
        }

        private void AssertConstructorHasBeenCalled()
        {
            if (this.grainFactory == null)
                throw new ArgumentException("Ctor was not called");
        }
    }
}
