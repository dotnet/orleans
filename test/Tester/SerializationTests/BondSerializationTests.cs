using Bond;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Xunit;

namespace UnitTests.Serialization
{
    using Orleans.Serialization;
    using System.Collections.Generic;
    using System.Reflection;
    
    public class BondSerializationTests
    {
        public BondSerializationTests()
        {
            SerializationManager.InitializeForTesting(new List<TypeInfo> { typeof(BondSerializer).GetTypeInfo() });
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SimpleBondSchemaSerializationTest()
        {
            var schema = new SimpleBondSchema { SomeValue = int.MaxValue };
            var output = SerializationManager.RoundTripSerializationForTesting(schema);
            Assert.AreNotSame(output, schema, "The serializer returned an instance of the same object");
            Assert.AreEqual(schema.SomeValue, output.SomeValue, "The serialization didn't preserve the proper value");
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SimpleGenericBondSchemaSerializationTest()
        {
            var schema = new SimpleGenericSchema<int> { SomeValue = int.MaxValue };
            var output = SerializationManager.RoundTripSerializationForTesting(schema);
            Assert.AreNotSame(output, schema, "The serializer returned an instance of the same object");
            Assert.AreEqual(schema.SomeValue, output.SomeValue, "The serialization didn't preserve the proper value");
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
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

            var output = SerializationManager.RoundTripSerializationForTesting(schema);
            Assert.IsNotNull(output);
            Assert.AreNotSame(output, schema, "The serializer returned an instance of the same object");
            Assert.AreEqual(schema.SomeValue.SomeValue.SomeValue, output.SomeValue.SomeValue.SomeValue, "The serialization didn't preserve the proper value");
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SimpleBondSchemaCopyTest()
        {
            var schema = new SimpleBondSchema { SomeValue = int.MaxValue };
            var output = SerializationManager.DeepCopy(schema) as SimpleBondSchema;
            Assert.AreNotSame(output, schema, "The serializer returned an instance of the same object");
            Assert.AreEqual(schema.SomeValue, output.SomeValue, "The serialization didn't preserve the proper value");
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SimpleGenericBondSchemaCopyTest()
        {
            var schema = new SimpleGenericSchema<int> { SomeValue = int.MaxValue };
            var output = SerializationManager.DeepCopy(schema) as SimpleGenericSchema<int>;
            Assert.AreNotSame(output, schema, "The serializer returned an instance of the same object");
            Assert.AreEqual(schema.SomeValue, output.SomeValue, "The serialization didn't preserve the proper value");
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
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

            var output = SerializationManager.DeepCopy(schema) as SimpleGenericSchema<SimpleGenericSchema<SimpleBondSchema>>;
            Assert.IsNotNull(output);
            Assert.AreNotSame(output, schema, "The serializer returned an instance of the same object");
            Assert.AreEqual(schema.SomeValue.SomeValue.SomeValue, output.SomeValue.SomeValue.SomeValue, "The serialization didn't preserve the proper value");
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
