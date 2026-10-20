#pragma warning disable CS0618 // These tests exercise runtime compatibility with the legacy token.

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
        public enum ScanDirection { Incoming, Outgoing }

        public enum RequestKind { OptedOut, InheritsDefault, NotRequestBase }

        [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
        [InlineData(ScanDirection.Incoming, RequestKind.OptedOut, 0)]
        [InlineData(ScanDirection.Incoming, RequestKind.InheritsDefault, 2)]
        [InlineData(ScanDirection.Incoming, RequestKind.NotRequestBase, 2)]
        [InlineData(ScanDirection.Outgoing, RequestKind.OptedOut, 0)]
        [InlineData(ScanDirection.Outgoing, RequestKind.InheritsDefault, 2)]
        [InlineData(ScanDirection.Outgoing, RequestKind.NotRequestBase, 2)]
        public void ArgumentsAreReadOnlyWhenRequestMayHaveToken(ScanDirection direction, RequestKind kind, int expectedReads)
        {
            ICountingRequest request = kind switch
            {
                RequestKind.OptedOut => new OptedOutRequest(),
                RequestKind.InheritsDefault => new DefaultRequest(),
                _ => new NonRequestBaseInvokable(),
            };

            if (direction == ScanDirection.Incoming)
            {
                CancellationSourcesExtension.RegisterCancellationTokens(target: null!, request);
            }
            else
            {
                GrainReferenceRuntime.SetGrainCancellationTokensTarget(cancellationTokenRuntime: null!, target: null!, request);
            }

            Assert.Equal(expectedReads, request.ArgumentReads);
        }

        private interface ICountingRequest : IInvokable
        {
            int ArgumentReads { get; }
        }

        private sealed class OptedOutRequest : DefaultRequest
        {
            public override bool MayHaveGrainCancellationToken => false;
        }

        private class DefaultRequest : RequestBase, ICountingRequest
        {
            public int ArgumentReads { get; private set; }

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

        private sealed class NonRequestBaseInvokable : ICountingRequest
        {
            public int ArgumentReads { get; private set; }

            public int GetArgumentCount() => 2;

            public object GetArgument(int index)
            {
                ArgumentReads++;
                return index;
            }

            public void SetArgument(int index, object value) => throw new NotSupportedException();
            public ValueTask<Response> Invoke() => throw new NotSupportedException();
            public object GetTarget() => throw new NotSupportedException();
            public void SetTarget(ITargetHolder holder) => throw new NotSupportedException();
            public void Dispose() { }
            public string GetMethodName() => "M";
            public string GetInterfaceName() => "I";
            public string GetActivityName() => "I/M";
            public Type GetInterfaceType() => typeof(object);
            public MethodInfo GetMethod() => throw new NotSupportedException();
        }
    }
}
