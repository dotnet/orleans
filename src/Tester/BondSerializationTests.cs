/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/


using Bond;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tester
{
    using Orleans.Serialization;

    [TestClass]
    public class BondSerializationTests
    {
        [TestInitialize]
        public void InitializeForTesting()
        {
            SerializationManager.InitializeForTesting();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SimpleBondSchemaSerializationTest()
        {
            var schema = new SimpleBondSchema { SomeValue = int.MaxValue };
            var output = SerializationManager.RoundTripSerializationForTesting(schema);
            Assert.AreNotSame(output, schema, "The serializer returned an instance of the same object");
            Assert.AreEqual(schema.SomeValue, output.SomeValue, "The serialization didn't preserve the proper value");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SimpleGenericBondSchemaSerializationTest()
        {
            var schema = new SimpleGenericSchema<int> { SomeValue = int.MaxValue };
            var output = SerializationManager.RoundTripSerializationForTesting(schema);
            Assert.AreNotSame(output, schema, "The serializer returned an instance of the same object");
            Assert.AreEqual(schema.SomeValue, output.SomeValue, "The serialization didn't preserve the proper value");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
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

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void RecursiveSerializationTest()
        {
            var schema = new RecursiveSchema();
            schema.SomeValue = schema;

            Assert.AreSame(schema, schema.SomeValue);
            var output = SerializationManager.RoundTripSerializationForTesting(schema);
            Assert.IsNotNull(output);
            Assert.AreNotSame(output, schema, "The serializer returned an instance of the same object");

            // ensure the output object and its child are references to the same object.
            Assert.AreSame(output, output.SomeValue);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SimpleBondSchemaCopyTest()
        {
            var schema = new SimpleBondSchema { SomeValue = int.MaxValue };
            var output = SerializationManager.DeepCopy(schema) as SimpleBondSchema;
            Assert.AreNotSame(output, schema, "The serializer returned an instance of the same object");
            Assert.AreEqual(schema.SomeValue, output.SomeValue, "The serialization didn't preserve the proper value");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SimpleGenericBondSchemaCopyTest()
        {
            var schema = new SimpleGenericSchema<int> { SomeValue = int.MaxValue };
            var output = SerializationManager.DeepCopy(schema) as SimpleGenericSchema<int>;
            Assert.AreNotSame(output, schema, "The serializer returned an instance of the same object");
            Assert.AreEqual(schema.SomeValue, output.SomeValue, "The serialization didn't preserve the proper value");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
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

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void RecursiveCopyTest()
        {
            var schema = new RecursiveSchema();
            schema.SomeValue = schema;

            Assert.AreSame(schema, schema.SomeValue);
            var output = SerializationManager.DeepCopy(schema) as RecursiveSchema;
            Assert.IsNotNull(output);
            Assert.AreNotSame(output, schema, "The serializer returned an instance of the same object");

            // ensure the output object and its child are references to the same object.
            Assert.AreSame(output, output.SomeValue);
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
