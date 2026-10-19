using System.Reflection;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;
using Xunit;

namespace UnitTests.CancellationTests
{
    /// <summary>
    /// Tests that the runtime inspects request arguments for <see cref="GrainCancellationToken"/> instances
    /// only when the request may have one.
    /// </summary>
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Runtime")]
    public class GrainCancellationTokenScanTests
    {
        [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
        [InlineData(true, 2)]
        [InlineData(false, 0)]
        public void RegisterCancellationTokensReadsArgumentsOnlyWhenRequestMayHaveToken(bool mayHaveToken, int expectedReads)
        {
            var request = new CountingRequest(mayHaveToken);

            CancellationSourcesExtension.RegisterCancellationTokens(target: null!, request);

            Assert.Equal(expectedReads, request.ArgumentReads);
        }

        private sealed class CountingRequest(bool mayHaveToken) : RequestBase
        {
            public int ArgumentReads { get; private set; }

            public override bool MayHaveGrainCancellationToken => mayHaveToken;

            public override int GetArgumentCount() => 2;

            public override object GetArgument(int index)
            {
                ArgumentReads++;
                return index;
            }

            public override ValueTask<Response> Invoke() => throw new NotSupportedException();
            public override object GetTarget() => throw new NotSupportedException();
            public override void SetTarget(ITargetHolder holder) => throw new NotSupportedException();
            public override void Dispose() { }
            public override string GetMethodName() => "M";
            public override string GetInterfaceName() => "I";
            public override string GetActivityName() => "I/M";
            public override Type GetInterfaceType() => typeof(object);
            public override MethodInfo GetMethod() => throw new NotSupportedException();
        }
    }
}
