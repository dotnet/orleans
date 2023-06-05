using System.Text;
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
            var grain = GrainFactory.GetGrain<IGeneratorTestGrain>(GetRandomGrainId(), grainName);

            var isNull = await grain.StringIsNullOrEmpty();
            Assert.True(isNull);

            await grain.StringSet("Begin");

            isNull = await grain.StringIsNullOrEmpty();
            Assert.False(isNull);

            var members = await grain.GetMemberVariables();
            Assert.Equal("Begin", members.stringVar);

            var encoding = new ASCIIEncoding();
            var bytes = encoding.GetBytes("ByteBegin");
            var str = "StringBegin";
            var memberVariables = new MemberVariables(bytes, str, ReturnCode.Fail);

            await grain.SetMemberVariables(memberVariables);

            members = await grain.GetMemberVariables();
            var enc = new ASCIIEncoding();

            Assert.Equal("ByteBegin", enc.GetString(members.byteArray));
            Assert.Equal("StringBegin", members.stringVar);
            Assert.Equal(ReturnCode.Fail, members.code);
        }

        [Fact]
        public async Task GeneratorDerivedGrain1ControlFlow()
        {
            var grain = GrainFactory.GetGrain<IGeneratorTestDerivedGrain1>(GetRandomGrainId());

            var isNull = await grain.StringIsNullOrEmpty();
            Assert.True(isNull);

            await grain.StringSet("Begin");

            isNull = await grain.StringIsNullOrEmpty();
            Assert.False(isNull);

            var members = await grain.GetMemberVariables();
            Assert.Equal("Begin", members.stringVar);

            var encoding = new ASCIIEncoding();
            var bytes = encoding.GetBytes("ByteBegin");
            var str = "StringBegin";
            var memberVariables = new MemberVariables(bytes, str, ReturnCode.Fail);

            await grain.SetMemberVariables(memberVariables);

            members = await grain.GetMemberVariables();
            var enc = new ASCIIEncoding();

            Assert.Equal("ByteBegin", enc.GetString(members.byteArray));
            Assert.Equal("StringBegin", members.stringVar);
            Assert.Equal(ReturnCode.Fail, members.code);
        }

        [Fact]
        public async Task GeneratorDerivedGrain2ControlFlow()
        {
            var grainName = typeof(GeneratorTestDerivedGrain2).FullName;
            var grain = GrainFactory.GetGrain<IGeneratorTestDerivedGrain2>(GetRandomGrainId(), grainName);

            var boolPromise = await grain.StringIsNullOrEmpty();
            Assert.True(boolPromise);

            await grain.StringSet("Begin");

            boolPromise = await grain.StringIsNullOrEmpty();
            Assert.False(boolPromise);

            var members = await grain.GetMemberVariables();
            Assert.Equal("Begin", members.stringVar);

            var encoding = new ASCIIEncoding();
            var bytes = encoding.GetBytes("ByteBegin");
            var str = "StringBegin";
            var memberVariables = new MemberVariables(bytes, str, ReturnCode.Fail);

            await grain.SetMemberVariables(memberVariables);

            members = await grain.GetMemberVariables();
            var enc = new ASCIIEncoding();

            Assert.Equal("ByteBegin", enc.GetString(members.byteArray));
            Assert.Equal("StringBegin", members.stringVar);
            Assert.Equal(ReturnCode.Fail, members.code);

            var strPromise = await grain.StringConcat("Begin", "Cont", "End");
            Assert.Equal("BeginContEnd", strPromise);
        }

        [Fact]
        public async Task GeneratorDerivedDerivedGrainControlFlow()
        {
            var grain = GrainFactory.GetGrain<IGeneratorTestDerivedDerivedGrain>(GetRandomGrainId());

            var isNull = await grain.StringIsNullOrEmpty();
            Assert.True(isNull);

            await grain.StringSet("Begin");

            isNull = await grain.StringIsNullOrEmpty();
            Assert.False(isNull);

            var members = await grain.GetMemberVariables();
            Assert.Equal("Begin", members.stringVar);

            var arguments = new ReplaceArguments("Begin", "End");
            var strPromise = await grain.StringReplace(arguments);
            Assert.Equal("End", strPromise);

            strPromise = await grain.StringConcat("Begin", "Cont", "End");
            Assert.Equal("BeginContEnd", strPromise);

            string[] strArray = { "Begin", "Cont", "Cont", "End" };
            strPromise = await grain.StringNConcat(strArray);
            Assert.Equal("BeginContContEnd", strPromise);

            var encoding = new ASCIIEncoding();
            var bytes = encoding.GetBytes("ByteBegin");
            var str = "StringBegin";
            var memberVariables = new MemberVariables(bytes, str, ReturnCode.Fail);

            await grain.SetMemberVariables(memberVariables);

            members = await grain.GetMemberVariables();

            var enc = new ASCIIEncoding();

            Assert.Equal("ByteBegin", enc.GetString(members.byteArray));
            Assert.Equal("StringBegin", members.stringVar);
            Assert.Equal(ReturnCode.Fail, members.code);
        }

        [Fact]
        public async Task CodeGenDerivedFromCSharpInterfaceInDifferentAssembly()
        {
            var grain = GrainFactory.GetGrain<IGeneratorTestDerivedFromCSharpInterfaceInExternalAssemblyGrain>(Guid.NewGuid());
            var input = 1;
            var output = await grain.Echo(input);
            Assert.Equal(input, output);
        }

        [Fact]
        public async Task GrainWithGenericMethods()
        {
            var grain = GrainFactory.GetGrain<IGrainWithGenericMethods>(Guid.NewGuid());
            Assert.Equal("default string", await grain.Default());
            Assert.Equal(-8, await grain.RoundTrip(8));
            Assert.Equal(new[] { typeof(IGrain), typeof(string), typeof(DateTime) }, await grain.GetTypesExplicit<IGrain, string, DateTime>());
            Assert.Equal(new[] { typeof(IGrain), typeof(string), typeof(DateTime) }, await grain.GetTypesInferred((IGrain)grain, default(string), default(DateTime)));
            Assert.Equal(new[] { typeof(IGrain), typeof(string) }, await grain.GetTypesInferred(default(IGrain), default(string), 0));
            var now = DateTime.Now;
            Assert.Equal(now, await grain.RoundTrip(now));
            Assert.Equal(default, await grain.Default<DateTime>());

            Assert.Equal(grain, await grain.Constraints(grain));
        }

        [Fact]
        public async Task GenericGrainWithGenericMethods()
        {
            var grain = GrainFactory.GetGrain<IGenericGrainWithGenericMethods<int>>(Guid.NewGuid());

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

            var grain = GrainFactory.GetGrain<IGrainWithGenericMethods>(Guid.NewGuid());
            var observer = GrainFactory.CreateObjectReference<IGrainObserverWithGenericMethods>(localObject);
            await grain.SetValueOnObserver(observer, "ToastedEnchiladas");
            Assert.Equal("ToastedEnchiladas", await localObject.ValueTask);
        }

        [Fact]
        public async Task GrainWithValueTaskMethod()
        {
            var grain = GrainFactory.GetGrain<IGrainWithGenericMethods>(Guid.NewGuid());
            Assert.Equal(1, await grain.ValueTaskMethod(true).ConfigureAwait(false));
            Assert.Equal(2, await grain.ValueTaskMethod(false).ConfigureAwait(false));
        }

        private class ObserverWithGenericMethods : IGrainObserverWithGenericMethods
        {
            private readonly TaskCompletionSource<object> valueCompletion = new TaskCompletionSource<object>();

            public Task<object> ValueTask => valueCompletion.Task;

            public void SetValue<T>(T value)
            {
                valueCompletion.SetResult(value);
            }
        }

        [Fact, TestCategory("FSharp")]
        public async Task CodeGenDerivedFromFSharpInterfaceInDifferentAssembly()
        {
            var grain = GrainFactory.GetGrain<IGeneratorTestDerivedFromFSharpInterfaceInExternalAssemblyGrain>(Guid.NewGuid());
            var input = 1;
            var output = await grain.Echo(input);
            Assert.Equal(input, output);
        }
    }
}
