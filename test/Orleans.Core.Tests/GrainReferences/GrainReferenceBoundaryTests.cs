using System.Reflection;
using NSubstitute;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Xunit;

namespace UnitTests.GrainReferences;

[TestCategory("BVT"), TestCategory("GrainReference")]
public class GrainReferenceBoundaryTests
{
    [Fact]
    public void InvocationRejectsNullBeforeRuntimeDispatch()
    {
        using var fixture = new ReferenceConstructionFixture();
        var runtime = Substitute.For<IGrainReferenceRuntime>();
        foreach (var reference in new[] { CreateReference(fixture, runtime), new BoundaryReference(null!, default) })
        {
            Assert.Throws<ArgumentNullException>("methodDescription", () => { _ = reference.CallWithResult(null!); });
            Assert.Throws<ArgumentNullException>("methodDescription", () => { _ = reference.CallAsync(null!); });
            Assert.Throws<ArgumentNullException>("methodDescription", () => reference.Call(null!));
        }

        Assert.Empty(runtime.ReceivedCalls());
    }

    [Fact]
    public async Task InvocationPreservesRequestOptionsAndRuntimeResults()
    {
        using var fixture = new ReferenceConstructionFixture();
        var runtime = Substitute.For<IGrainReferenceRuntime>();
        var reference = CreateReference(fixture, runtime);
        var request = Substitute.For<IRequest>();
        const InvokeMethodOptions options = InvokeMethodOptions.AlwaysInterleave | InvokeMethodOptions.OneWay;
        request.Options.Returns(options);
        var resultCompletion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.InvokeMethodAsync<string>(Arg.Is<GrainReference>(r => ReferenceEquals(r, reference)), request, options)
            .Returns(new ValueTask<string?>(resultCompletion.Task));
        runtime.InvokeMethodAsync(Arg.Is<GrainReference>(r => ReferenceEquals(r, reference)), request, options)
            .Returns(new ValueTask(completion.Task));

        var result = reference.CallWithResult(request);
        var pending = reference.CallAsync(request);
        reference.Call(request);

        Assert.False(result.IsCompleted);
        Assert.False(pending.IsCompleted);
        resultCompletion.SetResult("runtime-result");
        completion.SetResult();
        Assert.Equal("runtime-result", await result);
        await pending;
        await runtime.Received(1).InvokeMethodAsync<string>(Arg.Is<GrainReference>(r => ReferenceEquals(r, reference)), request, options);
        await runtime.Received(1).InvokeMethodAsync(Arg.Is<GrainReference>(r => ReferenceEquals(r, reference)), request, options);
        runtime.Received(1).InvokeMethod(Arg.Is<GrainReference>(r => ReferenceEquals(r, reference)), request, options);
        request.DidNotReceive().Dispose();
    }

    [Fact]
    public void InvocationPreservesSubmissionExceptionsAndRequestOwnership()
    {
        using var fixture = new ReferenceConstructionFixture();
        var runtime = Substitute.For<IGrainReferenceRuntime>();
        var reference = CreateReference(fixture, runtime);
        var request = Substitute.For<IRequest>();
        var failure = new InvalidOperationException("submission-failure");
        runtime.InvokeMethodAsync<string>(reference, request, InvokeMethodOptions.None).Returns<ValueTask<string?>>(_ => throw failure);
        runtime.InvokeMethodAsync(reference, request, InvokeMethodOptions.None).Returns(_ => throw failure);
        runtime.When(r => r.InvokeMethod(reference, request, InvokeMethodOptions.None)).Do(_ => throw failure);

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => { _ = reference.CallWithResult(request); }));
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => { _ = reference.CallAsync(request); }));
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => reference.Call(request)));
        request.DidNotReceive().Dispose();
    }

    [Fact]
    public async Task InvocationPreservesFaultedRuntimeResultsAndRequestOwnership()
    {
        using var fixture = new ReferenceConstructionFixture();
        var runtime = Substitute.For<IGrainReferenceRuntime>();
        var reference = CreateReference(fixture, runtime);
        var request = Substitute.For<IRequest>();
        var failure = new InvalidOperationException("runtime-failure");
        runtime.InvokeMethodAsync<string>(reference, request, InvokeMethodOptions.None)
            .Returns(new ValueTask<string?>(Task.FromException<string?>(failure)));
        runtime.InvokeMethodAsync(reference, request, InvokeMethodOptions.None)
            .Returns(new ValueTask(Task.FromException(failure)));

        var result = reference.CallWithResult(request);
        var pending = reference.CallAsync(request);

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => result.AsTask()));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => pending.AsTask()));
        request.DidNotReceive().Dispose();
    }

    [Fact]
    public void RequestFormattingRejectsNullRequests()
    {
        Assert.Throws<ArgumentNullException>("request", () => IRequest.ToString(null!));
        Assert.Throws<ArgumentNullException>("request", () => IRequest.ToMethodCallString(null!));
    }

    [Fact]
    public void RequestFormattingPreservesReflectedParameterTypesAndTarget()
    {
        var request = CreateFormattingRequest("value", null, 7);
        request.GetTarget().Returns("target");
        request.GetMethod().Returns(typeof(GrainReferenceBoundaryTests).GetMethod(
            nameof(FormattingSignature), BindingFlags.Static | BindingFlags.NonPublic)!);

        Assert.Equal("IBoundary[(IBoundary)target].Call(System.Object, System.String, System.Int32)", IRequest.ToString(request));
        Assert.Equal("IBoundary.Call(value, , 7)", IRequest.ToMethodCallString(request));
    }

    [Fact]
    public void RequestFormattingPreservesFallbackTypesAndNullArguments()
    {
        var request = CreateFormattingRequest("value", null, 7);

        Assert.Equal("IBoundary.Call(System.String, null, System.Int32)", IRequest.ToString(request));
        Assert.Equal("IBoundary.Call(value, , 7)", IRequest.ToMethodCallString(request));
    }

    [Fact]
    public void RequestFormattingPreservesEmptyArgumentLists()
    {
        var request = CreateFormattingRequest();

        Assert.Equal("IBoundary.Call()", IRequest.ToString(request));
        Assert.Equal("IBoundary.Call()", IRequest.ToMethodCallString(request));
    }

    [Fact]
    public void GrainReferencePreservesNullEqualityAndFormatting()
    {
        using var fixture = new ReferenceConstructionFixture();
        var reference = CreateReference(fixture, Substitute.For<IGrainReferenceRuntime>());
        var same = fixture.CreateReference("request-key");
        var different = fixture.CreateReference("other-key");
        GrainReference? missing = null;
        Assert.False(reference.Equals((GrainReference?)null));
        Assert.False(reference.Equals((object?)null));
        Assert.True(missing == null);
        Assert.False(missing != null);
        Assert.False(reference == missing);
        Assert.False(missing == reference);
        Assert.True(reference != missing);
        Assert.True(missing != reference);
        Assert.True(reference == same);
        Assert.False(reference != same);
        Assert.True(reference.Equals(same));
        Assert.False(reference == different);
        Assert.True(reference != different);
        Assert.Equal(same.GetHashCode(), reference.GetHashCode());
        Assert.Equal(same.GetUniformHashCode(), reference.GetUniformHashCode());
        Assert.Equal(17, reference.InterfaceVersion);
        Assert.Equal(ReferenceConstructionFixture.InterfaceType, reference.InterfaceType);
        const string text = "GrainReference:reference-construction/request-key:IConstructionGrain";
        Assert.Equal(text, reference.ToString());
        Assert.Equal(text, ((IFormattable)reference).ToString(null, null));
        Assert.Equal(text, ((IFormattable)reference).ToString("ignored", null));
        Span<char> destination = stackalloc char[text.Length];
        Assert.True(((ISpanFormattable)reference).TryFormat(destination, out var written, default, null));
        Assert.Equal(text.Length, written);
        Assert.Equal(text, destination.ToString());
        Assert.False(((ISpanFormattable)reference).TryFormat(destination[..^1], out written, default, null));
        Assert.Equal(0, written);
    }

    private static BoundaryReference CreateReference(ReferenceConstructionFixture fixture, IGrainReferenceRuntime runtime)
    {
        var shared = fixture.CreateReference("request-key").Shared;
        return new BoundaryReference(new GrainReferenceShared(
            shared.GrainType, shared.InterfaceType, shared.InterfaceVersion, runtime, InvokeMethodOptions.Unordered,
            shared.CodecProvider, shared.CopyContextPool, shared.ServiceProvider), IdSpan.Create("request-key"));
    }

    private static IRequest CreateFormattingRequest(params object?[] arguments)
    {
        var request = Substitute.For<IRequest>();
        request.GetInterfaceName().Returns("IBoundary");
        request.GetMethodName().Returns("Call");
        request.GetTarget().Returns((object?)null);
        request.GetMethod().Returns((MethodInfo)null!);
        request.GetArgumentCount().Returns(arguments.Length);
        request.GetArgument(Arg.Any<int>()).Returns(call => arguments[call.Arg<int>()]);
        return request;
    }

    // Reflection metadata intentionally differs from runtime argument types, including a null string argument.
    private static void FormattingSignature(object value, string? text, int number) { }

    private sealed class BoundaryReference(GrainReferenceShared shared, IdSpan key) : GrainReference(shared, key)
    {
        public ValueTask<string?> CallWithResult(IRequest request) => InvokeAsync<string>(request);
        public ValueTask CallAsync(IRequest request) => InvokeAsync(request);
        public void Call(IRequest request) => Invoke(request);
    }
}
