using Bond;
using Orleans.Runtime.Configuration;
using TestExtensions;
using Xunit;

namespace BondUtils.Tests.Serialization
{
    using System.Reflection;
    using Orleans;
    using Orleans.Configuration;
    using Orleans.Serialization;

    public class BondSerializationTests
    {
        private readonly SerializationTestEnvironment environment;

        public BondSerializationTests()
        {
            this.environment = SerializationTestEnvironment.InitializeWithDefaults(
                builder => builder.Configure<SerializationProviderOptions>(
                    options => options.SerializationProviders.Add(typeof(BondSerializer))));
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void SimpleBondSchemaSerializationTest()
        {
            var schema = new SimpleBondSchema { SomeValue = int.MaxValue };
            var output = this.environment.SerializationManager.RoundTripSerializationForTesting(schema);
            Assert.NotSame(output, schema); //The serializer returned an instance of the same object
            Assert.Equal(schema.SomeValue, output.SomeValue); //The serialization didn't preserve the proper value
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void SimpleGenericBondSchemaSerializationTest()
        {
            var schema = new SimpleGenericSchema<int> { SomeValue = int.MaxValue };
            var output = this.environment.SerializationManager.RoundTripSerializationForTesting(schema);
            Assert.NotSame(output, schema); //The serializer returned an instance of the same object
            Assert.Equal(schema.SomeValue, output.SomeValue); //The serialization didn't preserve the proper value
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void SimpleNestedGenericBondSchemaSerializationTest()
        {
            var schema = new SimpleGenericSchema<SimpleGenericSchema<SimpleBondSchema>>
            {
                SomeValue = new SimpleGenericSchema<SimpleBondSchema>
                {
                    SomeValue = new SimpleBondSchema
                    {
                        SomeValue = int.MaxValue
                    }
                }
            };

            var output = this.environment.SerializationManager.RoundTripSerializationForTesting(schema);
            Assert.NotNull(output);
            Assert.NotSame(output, schema); //The serializer returned an instance of the same object
            Assert.Equal(schema.SomeValue.SomeValue.SomeValue, output.SomeValue.SomeValue.SomeValue); //The serialization didn't preserve the proper value
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void SimpleBondSchemaCopyTest()
        {
            var schema = new SimpleBondSchema { SomeValue = int.MaxValue };
            var output = this.environment.SerializationManager.DeepCopy(schema) as SimpleBondSchema;
            Assert.NotSame(output, schema);  //The serializer returned an instance of the same object"
            Assert.Equal(schema.SomeValue, output.SomeValue); //The serialization didn't preserve the proper value
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void SimpleGenericBondSchemaCopyTest()
        {
            var schema = new SimpleGenericSchema<int> { SomeValue = int.MaxValue };
            var output = this.environment.SerializationManager.DeepCopy(schema) as SimpleGenericSchema<int>;
            Assert.NotSame(output, schema); //The serializer returned an instance of the same object
            Assert.Equal(schema.SomeValue, output.SomeValue); //The serialization didn't preserve the proper value
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void SimpleNestedGenericBondSchemaCopyTest()
        {
            var schema = new SimpleGenericSchema<SimpleGenericSchema<SimpleBondSchema>>
            {
                SomeValue = new SimpleGenericSchema<SimpleBondSchema>
                {
                    SomeValue = new SimpleBondSchema
                    {
                        SomeValue = int.MaxValue
                    }
                }
            };

            var output = this.environment.SerializationManager.DeepCopy(schema) as SimpleGenericSchema<SimpleGenericSchema<SimpleBondSchema>>;
            Assert.NotNull(output);
            Assert.NotSame(output, schema); //The serializer returned an instance of the same object
            Assert.Equal(schema.SomeValue.SomeValue.SomeValue, output.SomeValue.SomeValue.SomeValue); //The serialization didn't preserve the proper value
        }

        [Schema]
        public class SimpleBondSchema
        {
            [Id(0)]

            public int SomeValue;
        }

        [Schema]
        public class RecursiveSchema
        {
            [Id(0)]
            public RecursiveSchema SomeValue;
        }

        [Schema]
        public class SimpleGenericSchema<T>
        {
            [Id(0)]

            public T SomeValue;
        }
    }
}
