using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Serialization;
using TestGrainInterfaces;
using UnitTests.Tester;

namespace Tester.CodeGenTests
{
    using System;
    using System.Collections.Generic;

    using UnitTests.GrainInterfaces;

    /// <summary>
    /// Summary description for GrainClientTest
    /// </summary>
    [TestClass]
    public class GeneratorGrainTest : UnitTestSiloHost
    {
        [TestInitialize]
        public void InitializeForTesting()
        {
            SerializationManager.InitializeForTesting();
        }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            StopAllSilos();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        public async Task CodeGenRoundTripSerialization()
        {
            var grain = GrainClient.GrainFactory.GetGrain<ISerializationGenerationGrain>(GetRandomGrainId());

            // Test struct serialization.
            var expectedStruct = new SomeStruct(10) { Id = Guid.NewGuid(), PublicValue = 6, ValueWithPrivateGetter = 7 };
            expectedStruct.SetValueWithPrivateSetter(8);
            expectedStruct.SetPrivateValue(9);
            var actualStruct = await grain.RoundTripStruct(expectedStruct);
            Assert.AreEqual(expectedStruct.Id, actualStruct.Id);
            Assert.AreEqual(expectedStruct.ReadonlyField, actualStruct.ReadonlyField);
            Assert.AreEqual(expectedStruct.PublicValue, actualStruct.PublicValue);
            Assert.AreEqual(expectedStruct.ValueWithPrivateSetter, actualStruct.ValueWithPrivateSetter);
            Assert.AreEqual(expectedStruct.GetPrivateValue(), actualStruct.GetPrivateValue());
            Assert.AreEqual(expectedStruct.GetValueWithPrivateGetter(), actualStruct.GetValueWithPrivateGetter());

            // Test abstract class serialization.
            var expectedAbstract = new OuterClass.SomeConcreteClass { Int = 89, String = Guid.NewGuid().ToString() };
            expectedAbstract.Classes = new List<SomeAbstractClass>
            {
                expectedAbstract,
                new AnotherConcreteClass
                {
                    AnotherString = "hi",
                    Interfaces = new List<ISomeInterface> { expectedAbstract }
                }
            };
            var actualAbstract = await grain.RoundTripClass(expectedAbstract);
            Assert.AreEqual(expectedAbstract.Int, actualAbstract.Int);
            Assert.AreEqual(expectedAbstract.String, ((OuterClass.SomeConcreteClass)actualAbstract).String);
            Assert.AreEqual(expectedAbstract.Classes.Count, actualAbstract.Classes.Count);
            Assert.AreEqual(expectedAbstract.String, ((OuterClass.SomeConcreteClass)actualAbstract.Classes[0]).String);
            Assert.AreEqual(expectedAbstract.Classes[1].Interfaces[0].Int, actualAbstract.Classes[1].Interfaces[0].Int);

            // Test abstract class serialization with state.
            await grain.SetState(expectedAbstract);
            actualAbstract = await grain.GetState();
            Assert.AreEqual(expectedAbstract.Int, actualAbstract.Int);
            Assert.AreEqual(expectedAbstract.String, ((OuterClass.SomeConcreteClass)actualAbstract).String);
            Assert.AreEqual(expectedAbstract.Classes.Count, actualAbstract.Classes.Count);
            Assert.AreEqual(expectedAbstract.String, ((OuterClass.SomeConcreteClass)actualAbstract.Classes[0]).String);
            Assert.AreEqual(expectedAbstract.Classes[1].Interfaces[0].Int, actualAbstract.Classes[1].Interfaces[0].Int);

            // Test interface serialization.
            var expectedInterface = expectedAbstract;
            var actualInterface = await grain.RoundTripInterface(expectedInterface);
            Assert.AreEqual(expectedAbstract.Int, actualInterface.Int);
            
            // Test enum serialization.
            const SomeAbstractClass.SomeEnum ExpectedEnum = SomeAbstractClass.SomeEnum.Something;
            var actualEnum = await grain.RoundTripEnum(ExpectedEnum);
            Assert.AreEqual(ExpectedEnum, actualEnum);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("GetGrain")]
        public async Task GeneratorGrainControlFlow()
        {
            IGeneratorTestGrain grain = GrainClient.GrainFactory.GetGrain<IGeneratorTestGrain>(GetRandomGrainId(), "TestGrains.GeneratorTestGrain");
            
            bool isNull = await grain.StringIsNullOrEmpty();
            Assert.IsTrue(isNull);

            await grain.StringSet("Begin");
            
            isNull = await grain.StringIsNullOrEmpty();
            Assert.IsFalse(isNull);

            MemberVariables members = await grain.GetMemberVariables();
            Assert.AreEqual("Begin", members.stringVar);
            
            ASCIIEncoding encoding = new ASCIIEncoding();
            byte[] bytes = encoding.GetBytes("ByteBegin");
            string str = "StringBegin";
            MemberVariables memberVariables = new MemberVariables(bytes, str, ReturnCode.Fail);

            await grain.SetMemberVariables(memberVariables);

            members = await grain.GetMemberVariables();
            ASCIIEncoding enc = new ASCIIEncoding();

            Assert.AreEqual("ByteBegin", enc.GetString(members.byteArray));
            Assert.AreEqual("StringBegin", members.stringVar);
            Assert.AreEqual(ReturnCode.Fail, members.code);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("GetGrain")]
        public async Task GeneratorDerivedGrain1ControlFlow()
        {
            IGeneratorTestDerivedGrain1 grain = GrainClient.GrainFactory.GetGrain<IGeneratorTestDerivedGrain1>(GetRandomGrainId());
            
            bool isNull = await grain.StringIsNullOrEmpty();
            Assert.IsTrue(isNull);

            await grain.StringSet("Begin");

            isNull = await grain.StringIsNullOrEmpty();
            Assert.IsFalse(isNull);

            MemberVariables members = await grain.GetMemberVariables();
            Assert.AreEqual("Begin", members.stringVar);

            ASCIIEncoding encoding = new ASCIIEncoding();
            byte[] bytes = encoding.GetBytes("ByteBegin");
            string str = "StringBegin";
            MemberVariables memberVariables = new MemberVariables(bytes, str, ReturnCode.Fail);

            await grain.SetMemberVariables(memberVariables);

            members = await grain.GetMemberVariables();
            ASCIIEncoding enc = new ASCIIEncoding();

            Assert.AreEqual("ByteBegin", enc.GetString(members.byteArray));
            Assert.AreEqual("StringBegin", members.stringVar);
            Assert.AreEqual(ReturnCode.Fail, members.code);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("GetGrain")]
        public async Task GeneratorDerivedGrain2ControlFlow()
        {
            IGeneratorTestDerivedGrain2 grain = GrainClient.GrainFactory.GetGrain<IGeneratorTestDerivedGrain2>(GetRandomGrainId(), "TestGrains.GeneratorTestDerivedGrain2");

            bool boolPromise = await grain.StringIsNullOrEmpty();
            Assert.IsTrue(boolPromise);

            await grain.StringSet("Begin");

            boolPromise = await grain.StringIsNullOrEmpty();
            Assert.IsFalse(boolPromise);

            MemberVariables members = await grain.GetMemberVariables();
            Assert.AreEqual("Begin", members.stringVar);

            ASCIIEncoding encoding = new ASCIIEncoding();
            byte[] bytes = encoding.GetBytes("ByteBegin");
            string str = "StringBegin";
            MemberVariables memberVariables = new MemberVariables(bytes, str, ReturnCode.Fail);

            await grain.SetMemberVariables(memberVariables);

            members = await grain.GetMemberVariables();
            ASCIIEncoding enc = new ASCIIEncoding();

            Assert.AreEqual("ByteBegin", enc.GetString(members.byteArray));
            Assert.AreEqual("StringBegin", members.stringVar);
            Assert.AreEqual(ReturnCode.Fail, members.code);

            string strPromise = await grain.StringConcat("Begin", "Cont", "End");
            Assert.AreEqual("BeginContEnd", strPromise);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("GetGrain")]
        public async Task GeneratorDerivedDerivedGrainControlFlow()
        {
            IGeneratorTestDerivedDerivedGrain grain = GrainClient.GrainFactory.GetGrain<IGeneratorTestDerivedDerivedGrain>(GetRandomGrainId());
            
            bool isNull = await grain.StringIsNullOrEmpty();
            Assert.IsTrue(isNull);

            await grain.StringSet("Begin");

            isNull = await grain.StringIsNullOrEmpty();
            Assert.IsFalse(isNull);

            MemberVariables members = await grain.GetMemberVariables();
            Assert.AreEqual("Begin", members.stringVar);

            ReplaceArguments arguments = new ReplaceArguments("Begin", "End");
            string strPromise = await grain.StringReplace(arguments);
            Assert.AreEqual("End", strPromise);

            strPromise = await grain.StringConcat("Begin", "Cont", "End");
            Assert.AreEqual("BeginContEnd", strPromise);

            string[] strArray = { "Begin", "Cont", "Cont", "End" };
            strPromise = await grain.StringNConcat(strArray);
            Assert.AreEqual("BeginContContEnd", strPromise);

            ASCIIEncoding encoding = new ASCIIEncoding();
            byte[] bytes = encoding.GetBytes("ByteBegin");
            string str = "StringBegin";
            MemberVariables memberVariables = new MemberVariables(bytes, str, ReturnCode.Fail);

            await grain.SetMemberVariables(memberVariables);

            members = await grain.GetMemberVariables();

            ASCIIEncoding enc = new ASCIIEncoding();

            Assert.AreEqual("ByteBegin", enc.GetString(members.byteArray));
            Assert.AreEqual("StringBegin", members.stringVar);
            Assert.AreEqual(ReturnCode.Fail, members.code);
        }
    }
}
