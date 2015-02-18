using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using GeneratorTestGrain;

namespace UnitTests
{
    /// <summary>
    /// Summary description for GrainClientTest
    /// </summary>
    [TestClass]
    public class GeneratorGrainTest : UnitTestBase
    {
        [ClassCleanup]
        public static void MyClassCleanup()
        {
            ResetDefaultRuntimes();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("GetGrain")]
        public async Task GeneratorGrainControlFlow()
        {
            IGeneratorTestGrain grain = GeneratorTestGrainFactory.GetGrain(GetRandomGrainId(), "GeneratorTestGrain.GeneratorTestGrain");
            
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

        [TestMethod, TestCategory("Nightly"), TestCategory("GetGrain")]
        public async Task GeneratorDerivedGrain1ControlFlow()
        {
            IGeneratorTestDerivedGrain1 grain = GeneratorTestDerivedGrain1Factory.GetGrain(GetRandomGrainId());
            
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

        [TestMethod, TestCategory("Nightly"), TestCategory("GetGrain")]
        public async Task GeneratorDerivedGrain2ControlFlow()
        {
            IGeneratorTestDerivedGrain2 grain = GeneratorTestDerivedGrain2Factory.GetGrain(GetRandomGrainId(), "GeneratorTestGrain.GeneratorTestDerivedGrain2");

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

        [TestMethod, TestCategory("Nightly"), TestCategory("GetGrain")]
        public async Task GeneratorDerivedDerivedGrainControlFlow()
        {
            IGeneratorTestDerivedDerivedGrain grain = GeneratorTestDerivedDerivedGrainFactory.GetGrain(GetRandomGrainId());
            
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
