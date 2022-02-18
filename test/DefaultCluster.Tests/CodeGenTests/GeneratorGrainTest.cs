using System;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace Tester.CodeGenTests
{
    /// <summary>
    /// Summary description for GrainClientTest
    /// </summary>
    [TestCategory("BVT"), TestCategory("CodeGen")]
    public class GeneratorGrainTest : HostedTestClusterEnsureDefaultStarted
    {
        public GeneratorGrainTest(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        [Fact]
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

        [Fact]
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

        [Fact]
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

        [Fact]
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

        [Fact]
        public async Task CodeGenDerivedFromCSharpInterfaceInDifferentAssembly()
        {
            var grain = this.GrainFactory.GetGrain<IGeneratorTestDerivedFromCSharpInterfaceInExternalAssemblyGrain>(Guid.NewGuid());
            var input = 1;
            var output = await grain.Echo(input);
            Assert.Equal(input, output);
        }

        [Fact]
        public async Task GrainWithGenericMethods()
        {
            var grain = this.GrainFactory.GetGrain<IGrainWithGenericMethods>(Guid.NewGuid());
            Assert.Equal("default string", await grain.Default());
            Assert.Equal(-8, await grain.RoundTrip(8));
            Assert.Equal(new[] { typeof(IGrain), typeof(string), typeof(DateTime) }, await grain.GetTypesExplicit<IGrain, string, DateTime>());
            Assert.Equal(new[] { typeof(IGrain), typeof(string), typeof(DateTime) }, await grain.GetTypesInferred((IGrain)grain, default(string), default(DateTime)));
            Assert.Equal(new[] { typeof(IGrain), typeof(string) }, await grain.GetTypesInferred(default(IGrain), default(string), 0));
            var now = DateTime.Now;
            Assert.Equal(now, await grain.RoundTrip(now));
            Assert.Equal(default(DateTime), await grain.Default<DateTime>());

            Assert.Equal(grain, await grain.Constraints(grain));
        }

        [Fact]
        public async Task GenericGrainWithGenericMethods()
        {
            var grain = this.GrainFactory.GetGrain<IGenericGrainWithGenericMethods<int>>(Guid.NewGuid());

            // The non-generic version of the method returns default(T).
            Assert.Equal(0, await grain.Method(888));

            // The generic version of the method returns the value provided.
            var now = DateTime.Now;
            Assert.Equal(now, await grain.Method(now));
        }

        [Fact]
        public async Task GrainObserverWithGenericMethods()
        {
            var localObject = new ObserverWithGenericMethods();

            var grain = this.GrainFactory.GetGrain<IGrainWithGenericMethods>(Guid.NewGuid());
            var observer = this.GrainFactory.CreateObjectReference<IGrainObserverWithGenericMethods>(localObject);
            await grain.SetValueOnObserver(observer, "ToastedEnchiladas");
            Assert.Equal("ToastedEnchiladas", await localObject.ValueTask);
        }

        [Fact]
        public async Task GrainWithValueTaskMethod()
        {
            var grain = this.GrainFactory.GetGrain<IGrainWithGenericMethods>(Guid.NewGuid());
            Assert.Equal(1, await grain.ValueTaskMethod(true).ConfigureAwait(false));
            Assert.Equal(2, await grain.ValueTaskMethod(false).ConfigureAwait(false));
        }

        private class ObserverWithGenericMethods : IGrainObserverWithGenericMethods
        {
            private readonly TaskCompletionSource<object> valueCompletion = new TaskCompletionSource<object>();

            public Task<object> ValueTask => this.valueCompletion.Task;

            public void SetValue<T>(T value)
            {
                this.valueCompletion.SetResult(value);
            }
        }

        [Fact, TestCategory("FSharp")]
        public async Task CodeGenDerivedFromFSharpInterfaceInDifferentAssembly()
        {
            var grain = this.GrainFactory.GetGrain<IGeneratorTestDerivedFromFSharpInterfaceInExternalAssemblyGrain>(Guid.NewGuid());
            var input = 1;
            var output = await grain.Echo(input);
            Assert.Equal(input, output);
        }
    }
}
