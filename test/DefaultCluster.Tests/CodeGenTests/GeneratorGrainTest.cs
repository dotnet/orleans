using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace Tester.CodeGenTests
{
    /// <summary>
    /// Summary description for GrainClientTest
    /// </summary>
    public class GeneratorGrainTest : HostedTestClusterEnsureDefaultStarted
    {
        public GeneratorGrainTest(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        public async Task CodeGenRoundTripSerialization()
        {
            var grain = this.GrainFactory.GetGrain<ISerializationGenerationGrain>(GetRandomGrainId());

            // Test struct serialization.
            var expectedStruct = new SomeStruct(10) { Id = Guid.NewGuid(), PublicValue = 6, ValueWithPrivateGetter = 7 };
            expectedStruct.SetValueWithPrivateSetter(8);
            expectedStruct.SetPrivateValue(9);
            var actualStruct = await grain.RoundTripStruct(expectedStruct);
            Assert.Equal(expectedStruct.Id, actualStruct.Id);
            Assert.Equal(expectedStruct.ReadonlyField, actualStruct.ReadonlyField);
            Assert.Equal(expectedStruct.PublicValue, actualStruct.PublicValue);
            Assert.Equal(expectedStruct.ValueWithPrivateSetter, actualStruct.ValueWithPrivateSetter);
            Assert.Equal(expectedStruct.GetPrivateValue(), actualStruct.GetPrivateValue());
            Assert.Equal(expectedStruct.GetValueWithPrivateGetter(), actualStruct.GetValueWithPrivateGetter());

            // Test abstract class serialization.
            var input = new OuterClass.SomeConcreteClass { Int = 89, String = Guid.NewGuid().ToString() };
            input.Classes = new SomeAbstractClass[]
            {
                input,
                new AnotherConcreteClass
                {
                    AnotherString = "hi",
                    Interfaces = new List<ISomeInterface> { input }
                }
            };
            input.SetObsoleteInt(38);
            input.Enum = SomeAbstractClass.SomeEnum.SomethingElse;
            input.NonSerializedInt = 39;

            var output = await grain.RoundTripClass(input);

            Assert.Equal(input.Int, output.Int);
            Assert.Equal(input.Enum, output.Enum);
            Assert.Equal(input.String, ((OuterClass.SomeConcreteClass)output).String);
            Assert.Equal(input.Classes.Length, output.Classes.Length);
            Assert.Equal(input.String, ((OuterClass.SomeConcreteClass)output.Classes[0]).String);
            Assert.Equal(input.Classes[1].Interfaces[0].Int, output.Classes[1].Interfaces[0].Int);
            Assert.Equal(input.GetObsoleteInt(), output.GetObsoleteInt());
            
            Assert.Equal(0, output.NonSerializedInt);

            // Test abstract class serialization with state.
            await grain.SetState(input);
            output = await grain.GetState();
            Assert.Equal(input.Int, output.Int);
            Assert.Equal(input.String, ((OuterClass.SomeConcreteClass)output).String);
            Assert.Equal(input.Classes.Length, output.Classes.Length);
            Assert.Equal(input.String, ((OuterClass.SomeConcreteClass)output.Classes[0]).String);
            Assert.Equal(input.Classes[1].Interfaces[0].Int, output.Classes[1].Interfaces[0].Int);
            Assert.Equal(input.GetObsoleteInt(), output.GetObsoleteInt());
            Assert.Equal(0, output.NonSerializedInt);

            // Test interface serialization.
            var expectedInterface = input;
            var actualInterface = await grain.RoundTripInterface(expectedInterface);
            Assert.Equal(input.Int, actualInterface.Int);
            
            // Test enum serialization.
            const SomeAbstractClass.SomeEnum ExpectedEnum = SomeAbstractClass.SomeEnum.Something;
            var actualEnum = await grain.RoundTripEnum(ExpectedEnum);
            Assert.Equal(ExpectedEnum, actualEnum);

            // Test serialization of a generic class which has a value-type constraint.
            var expectedStructConstraintObject = new ClassWithStructConstraint<int> { Value = 38 };
            var actualStructConstraintObject =
                (ClassWithStructConstraint<int>)await grain.RoundTripObject(expectedStructConstraintObject);
            Assert.Equal(expectedStructConstraintObject.Value, actualStructConstraintObject.Value);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("GetGrain")]
        public async Task GeneratorGrainControlFlow()
        {
            var grainName = typeof(GeneratorTestGrain).FullName;
            IGeneratorTestGrain grain = this.GrainFactory.GetGrain<IGeneratorTestGrain>(GetRandomGrainId(), grainName);
            
            bool isNull = await grain.StringIsNullOrEmpty();
            Assert.True(isNull);

            await grain.StringSet("Begin");
            
            isNull = await grain.StringIsNullOrEmpty();
            Assert.False(isNull);

            MemberVariables members = await grain.GetMemberVariables();
            Assert.Equal("Begin", members.stringVar);
            
            ASCIIEncoding encoding = new ASCIIEncoding();
            byte[] bytes = encoding.GetBytes("ByteBegin");
            string str = "StringBegin";
            MemberVariables memberVariables = new MemberVariables(bytes, str, ReturnCode.Fail);

            await grain.SetMemberVariables(memberVariables);

            members = await grain.GetMemberVariables();
            ASCIIEncoding enc = new ASCIIEncoding();

            Assert.Equal("ByteBegin", enc.GetString(members.byteArray));
            Assert.Equal("StringBegin", members.stringVar);
            Assert.Equal(ReturnCode.Fail, members.code);
        }

        [Fact, TestCategory("Functional"), TestCategory("GetGrain")]
        public async Task GeneratorDerivedGrain1ControlFlow()
        {
            IGeneratorTestDerivedGrain1 grain = this.GrainFactory.GetGrain<IGeneratorTestDerivedGrain1>(GetRandomGrainId());
            
            bool isNull = await grain.StringIsNullOrEmpty();
            Assert.True(isNull);

            await grain.StringSet("Begin");

            isNull = await grain.StringIsNullOrEmpty();
            Assert.False(isNull);

            MemberVariables members = await grain.GetMemberVariables();
            Assert.Equal("Begin", members.stringVar);

            ASCIIEncoding encoding = new ASCIIEncoding();
            byte[] bytes = encoding.GetBytes("ByteBegin");
            string str = "StringBegin";
            MemberVariables memberVariables = new MemberVariables(bytes, str, ReturnCode.Fail);

            await grain.SetMemberVariables(memberVariables);

            members = await grain.GetMemberVariables();
            ASCIIEncoding enc = new ASCIIEncoding();

            Assert.Equal("ByteBegin", enc.GetString(members.byteArray));
            Assert.Equal("StringBegin", members.stringVar);
            Assert.Equal(ReturnCode.Fail, members.code);
        }

        [Fact, TestCategory("Functional"), TestCategory("GetGrain")]
        public async Task GeneratorDerivedGrain2ControlFlow()
        {
            var grainName = typeof(GeneratorTestDerivedGrain2).FullName;
            IGeneratorTestDerivedGrain2 grain = this.GrainFactory.GetGrain<IGeneratorTestDerivedGrain2>(GetRandomGrainId(), grainName);

            bool boolPromise = await grain.StringIsNullOrEmpty();
            Assert.True(boolPromise);

            await grain.StringSet("Begin");

            boolPromise = await grain.StringIsNullOrEmpty();
            Assert.False(boolPromise);

            MemberVariables members = await grain.GetMemberVariables();
            Assert.Equal("Begin", members.stringVar);

            ASCIIEncoding encoding = new ASCIIEncoding();
            byte[] bytes = encoding.GetBytes("ByteBegin");
            string str = "StringBegin";
            MemberVariables memberVariables = new MemberVariables(bytes, str, ReturnCode.Fail);

            await grain.SetMemberVariables(memberVariables);

            members = await grain.GetMemberVariables();
            ASCIIEncoding enc = new ASCIIEncoding();

            Assert.Equal("ByteBegin", enc.GetString(members.byteArray));
            Assert.Equal("StringBegin", members.stringVar);
            Assert.Equal(ReturnCode.Fail, members.code);

            string strPromise = await grain.StringConcat("Begin", "Cont", "End");
            Assert.Equal("BeginContEnd", strPromise);
        }

        [Fact, TestCategory("Functional"), TestCategory("GetGrain")]
        public async Task GeneratorDerivedDerivedGrainControlFlow()
        {
            IGeneratorTestDerivedDerivedGrain grain = this.GrainFactory.GetGrain<IGeneratorTestDerivedDerivedGrain>(GetRandomGrainId());
            
            bool isNull = await grain.StringIsNullOrEmpty();
            Assert.True(isNull);

            await grain.StringSet("Begin");

            isNull = await grain.StringIsNullOrEmpty();
            Assert.False(isNull);

            MemberVariables members = await grain.GetMemberVariables();
            Assert.Equal("Begin", members.stringVar);

            ReplaceArguments arguments = new ReplaceArguments("Begin", "End");
            string strPromise = await grain.StringReplace(arguments);
            Assert.Equal("End", strPromise);

            strPromise = await grain.StringConcat("Begin", "Cont", "End");
            Assert.Equal("BeginContEnd", strPromise);

            string[] strArray = { "Begin", "Cont", "Cont", "End" };
            strPromise = await grain.StringNConcat(strArray);
            Assert.Equal("BeginContContEnd", strPromise);

            ASCIIEncoding encoding = new ASCIIEncoding();
            byte[] bytes = encoding.GetBytes("ByteBegin");
            string str = "StringBegin";
            MemberVariables memberVariables = new MemberVariables(bytes, str, ReturnCode.Fail);

            await grain.SetMemberVariables(memberVariables);

            members = await grain.GetMemberVariables();

            ASCIIEncoding enc = new ASCIIEncoding();

            Assert.Equal("ByteBegin", enc.GetString(members.byteArray));
            Assert.Equal("StringBegin", members.stringVar);
            Assert.Equal(ReturnCode.Fail, members.code);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        public async Task CodeGenDerivedFromCSharpInterfaceInDifferentAssembly()
        {
            var grain = this.GrainFactory.GetGrain<IGeneratorTestDerivedFromCSharpInterfaceInExternalAssemblyGrain>(Guid.NewGuid());
            var input = 1;
            var output = await grain.Echo(input);
            Assert.Equal(input, output);
        }

#if !NETSTANDARD_TODO
        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("FSharp")]
        public async Task CodeGenDerivedFromFSharpInterfaceInDifferentAssembly()
        {
            var grain = this.GrainFactory.GetGrain<IGeneratorTestDerivedFromFSharpInterfaceInExternalAssemblyGrain>(Guid.NewGuid());
            var input = 1;
            var output = await grain.Echo(input);
            Assert.Equal(input, output);
        }
#endif
    }
}
