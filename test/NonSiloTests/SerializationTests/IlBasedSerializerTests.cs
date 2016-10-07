namespace UnitTests.Serialization
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using Orleans.Serialization;

    using UnitTests.GrainInterfaces;

    using Xunit;

    public class IlBasedSerializerTests
    {
        public IlBasedSerializerTests()
        {
            SerializationManager.InitializeForTesting(new List<TypeInfo>
            {
                typeof(IlBasedFallbackSerializer).GetTypeInfo()
            });
        }

        /// <summary>
        /// Tests that <see cref="IlBasedSerializerGenerator"/> supports distinct field selection for serialization
        /// versus copy operations.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void IlBasedSerializer_AllowCopiedFieldsToDifferFromSerializedFields()
        {
            var input = new FieldTest
            {
                One = 1,
                Two = 2,
                Three = 3
            };

            var generator = new IlBasedSerializerGenerator();
            var serializers = generator.GenerateSerializer(input.GetType(), f => f.Name != "One", f => f.Name != "Three");
            var copy = (FieldTest)serializers.DeepCopy(input);
            Assert.Equal(1, copy.One);
            Assert.Equal(2, copy.Two);
            Assert.Equal(0, copy.Three);

            var writer = new BinaryTokenStreamWriter();
            serializers.Serialize(input, writer, input.GetType());
            var reader = new BinaryTokenStreamReader(writer.ToByteArray());
            var deserialized = (FieldTest)serializers.Deserialize(input.GetType(), reader);

            Assert.Equal(0, deserialized.One);
            Assert.Equal(2, deserialized.Two);
            Assert.Equal(3, deserialized.Three);
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void IlBasedSerializer_CanSerializeComplexClassTest()
        {
            var input = OuterClass.GetPrivateClassInstance();
            input.Int = 89;
            input.String = Guid.NewGuid().ToString();
            input.NonSerializedInt = 39;
            input.Classes = new SomeAbstractClass[]
            {
                input,
                new AnotherConcreteClass
                {
                    AnotherString = "hi",
                    Interfaces = new List<ISomeInterface> { input }
                }
            };
            input.Enum = SomeAbstractClass.SomeEnum.Something;
            input.SetObsoleteInt(38);
            
            var output = (SomeAbstractClass)BuiltInSerializerTests.OrleansSerializationLoop(input);

            Assert.Equal(input.Int, output.Int);
            Assert.Equal(input.Enum, output.Enum);
            Assert.Equal(input.String, ((OuterClass.SomeConcreteClass)output).String);
            Assert.Equal(input.Classes.Length, output.Classes.Length);
            Assert.Equal(input.String, ((OuterClass.SomeConcreteClass)output.Classes[0]).String);
            Assert.Equal(input.Classes[1].Interfaces[0].Int, output.Classes[1].Interfaces[0].Int);
            Assert.Equal(0, output.NonSerializedInt);
            Assert.Equal(input.GetObsoleteInt(), output.GetObsoleteInt());
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void IlBasedSerializer_CanSerializeStructTest()
        {
            // Test struct serialization.
            var expectedStruct = new SomeStruct(10) { Id = Guid.NewGuid(), PublicValue = 6, ValueWithPrivateGetter = 7 };
            expectedStruct.SetValueWithPrivateSetter(8);
            expectedStruct.SetPrivateValue(9);
            var actualStruct = (SomeStruct)BuiltInSerializerTests.OrleansSerializationLoop(expectedStruct);
            Assert.Equal(expectedStruct.Id, actualStruct.Id);
            Assert.Equal(expectedStruct.ReadonlyField, actualStruct.ReadonlyField);
            Assert.Equal(expectedStruct.PublicValue, actualStruct.PublicValue);
            Assert.Equal(expectedStruct.ValueWithPrivateSetter, actualStruct.ValueWithPrivateSetter);
            Assert.Equal(expectedStruct.GetPrivateValue(), actualStruct.GetPrivateValue());
            Assert.Equal(expectedStruct.GetValueWithPrivateGetter(), actualStruct.GetValueWithPrivateGetter());
        }

        private class FieldTest
        {
#pragma warning disable 169
            public int One;
            public int Two;
            public int Three;
#pragma warning restore 169
        }
    }
}