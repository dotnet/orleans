using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Net;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.GrainReferences;
using Orleans.Metadata;
using Orleans.Networking.Shared;
using Orleans.Placement.Repartitioning;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.Serialization;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
[TestCategory("BVT"), TestCategory("Serialization")]
public sealed class SelfWritingResponseOwnershipTests
{
    [Theory]
    [InlineData(false, false, "Distinct")]
    [InlineData(false, true, "Distinct")]
    [InlineData(true, false, "Distinct")]
    [InlineData(true, true, "Distinct")]
    [InlineData(false, false, "Same")]
    [InlineData(false, true, "Same")]
    [InlineData(true, false, "Same")]
    [InlineData(true, true, "Same")]
    [InlineData(false, false, "Throw")]
    [InlineData(false, true, "Throw")]
    [InlineData(true, false, "Throw")]
    [InlineData(true, true, "Throw")]
    public async Task GeneratedCompatibilityFallback_TransfersOrReturnsOriginalExactlyOnce(bool observer, bool filtered, string behavior)
    {
        var counts = new Counts { ReturnInput = behavior == "Same", ThrowCopy = behavior == "Throw" };
        var values = new List<int> { 17, 23, 41 };
        var target = Substitute.For<IConcurrentGrain>();
        target.ModifyReturnList_Test().Returns(Task.FromResult(values));
        var request = typeof(IConcurrentGrain).Assembly.GetTypes()
            .Where(static type => !type.IsAbstract && !type.ContainsGenericParameters && typeof(IInvokable).IsAssignableFrom(type)
                && type.Name.StartsWith("Invokable_IConcurrentGrain_", StringComparison.Ordinal))
            .Select(static type => (IInvokable)Activator.CreateInstance(type)!)
            .Single(static request => request.GetMethodName() == nameof(IConcurrentGrain.ModifyReturnList_Test));
        Assert.IsAssignableFrom<IResponseInvokable>(request);
        var filter = filtered ? new CallbackFilter(context => context.Invoke()) : null;
        await using var fixture = new SendFixture(counts, filter, target);
        using var response = await fixture.Invoke(request, observer);
        Assert.Equal(filtered && behavior != "Throw" ? 2 : 1, counts.ResponseCopies);
        Assert.NotNull(counts.FallbackOriginal);
        Assert.False(fixture.Provider.TryGetRawResponseReader(typeof(List<int>), out _));
        if (behavior == "Same")
        {
            Assert.Same(counts.FallbackOriginal, response);
            Assert.Same(values, response.Result);
            Assert.Equal(0, counts.PayloadCopies);
            Assert.False(counts.OriginalWasPooledAtSend);
        }
        else
        {
            Assert.Null(counts.FallbackOriginal.TypedResult);
            Assert.True(counts.OriginalWasPooledAtSend);
            Assert.True(counts.OriginalWasReturnedOnce);
            Assert.Equal(filtered && behavior != "Throw" ? 2 : 1, counts.PayloadCopies);
            if (behavior == "Throw")
            {
                Assert.Same(counts.CopyFailure, response.Exception);
            }
            else
            {
                Assert.Null(response.Exception);
                Assert.NotSame(counts.FallbackOriginal, response);
                Assert.NotSame(values, response.Result);
                values.Clear();
                Assert.Equal(new[] { 17, 23, 41 }, Assert.IsType<List<int>>(response.Result));
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DirectResponse_CopiesAtInvocationAndAfterFiltersWithOwnedHolders(bool observer, bool filtered)
    {
        var counts = new Counts();
        var payload = new Payload { Values = [17, 23, 41] };
        var filter = filtered ? new CallbackFilter(async context =>
        {
            await context.Invoke();
            Assert.Equal(1, counts.PayloadCopies);
            Assert.NotSame(payload, context.Result);
            payload.Values[0] = 99;
            payload.Values.Add(73);
        }) : null;
        await using var fixture = new SendFixture(counts, filter);
        var request = new DirectRequest(payload, counts);

        using var response = await fixture.Invoke(request, observer);

        Assert.Null(response.Exception);
        Assert.Equal(filtered ? 2 : 1, counts.PayloadCopies);
        Assert.Equal(filtered ? 2 : 1, counts.Rents);
        if (filtered) Assert.NotSame(request.ReturnedResponse, response);
        else Assert.Same(request.ReturnedResponse, response);
        Assert.Equal(filtered ? 1 : 0, counts.Returns);
        Assert.Equal(filtered ? 1 : 0, counts.ResponseCopies);
        Assert.NotSame(payload, response.Result);
        var result = Assert.IsType<Payload>(response.Result);
        Assert.Equal(new[] { 17, 23, 41 }, result.Values);
        payload.Values.Clear();
        Assert.Equal(new[] { 17, 23, 41 }, result.Values);
        await fixture.AssertFrameRoundTrip(response);
        response.Dispose();
        Assert.Equal(counts.Rents, counts.Returns);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LegacyResponse_CopiesAtInvocationAndAfterFiltersAndReturnsOwnedWrappers(bool observer, bool filtered)
    {
        var counts = new Counts();
        var payload = new Payload { Values = [17, 23, 41] };
        var filter = filtered ? new CallbackFilter(async context =>
        {
            await context.Invoke();
            Assert.Equal(1, counts.PayloadCopies);
            payload.Values.Clear();
            Assert.Equal(new[] { 17, 23, 41 }, Assert.IsType<Payload>(context.Result).Values);
        }) : null;
        await using var fixture = new SendFixture(counts, filter);
        var request = new LegacyRequest(payload, counts);

        using var response = await fixture.Invoke(request, observer);

        Assert.Null(response.Exception);
        Assert.NotSame(request.ReturnedResponse, response);
        Assert.Equal(filtered ? 2 : 1, counts.PayloadCopies);
        Assert.Equal(filtered ? 2 : 1, counts.ResponseCopies);
        Assert.Equal(filtered ? 3 : 2, counts.Rents);
        Assert.Equal(filtered ? 2 : 1, counts.Returns);
        Assert.Equal(new[] { 17, 23, 41 }, Assert.IsType<Payload>(response.Result).Values);
        response.Dispose();
        Assert.Equal(counts.Rents, counts.Returns);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task FilterMutatesExistingResponse_IsolatesEnvelopeAndNestedReferences(bool observer, bool direct, bool replaceResult)
    {
        var counts = new Counts();
        var original = new Payload { Values = [17, 23, 41] };
        var replacement = new Payload { Values = [5, 8, 13] };
        Payload filterResult = null!;
        var filter = new CallbackFilter(async context =>
        {
            await context.Invoke();
            var response = context.Response;
            if (replaceResult) response!.Result = replacement;
            else Assert.IsType<Payload>(context.Result).Values = replacement.Values;
            Assert.Same(response, context.Response);
            filterResult = Assert.IsType<Payload>(context.Result);
            Assert.Same(replacement.Values, filterResult.Values);
        });
        await using var fixture = new SendFixture(counts, filter);
        LegacyRequest request = direct ? new DirectRequest(original, counts) : new LegacyRequest(original, counts);

        using var response = await fixture.Invoke(request, observer);

        Assert.Null(response.Exception);
        var result = Assert.IsType<Payload>(response.Result);
        Assert.NotSame(filterResult, result);
        Assert.NotSame(replacement.Values, result.Values);
        Assert.Equal(new[] { 5, 8, 13 }, result.Values);
        Assert.Equal(2, counts.PayloadCopies);
        Assert.Equal(direct ? 1 : 2, counts.ResponseCopies);
        Assert.Equal(direct ? 2 : 3, counts.Rents);
        Assert.Equal(counts.Rents - 1, counts.Returns);
        replacement.Values.Clear();
        Assert.Equal(new[] { 5, 8, 13 }, result.Values);
        await fixture.AssertFrameRoundTrip(response);
        response.Dispose();
        Assert.Equal(counts.Rents, counts.Returns);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FilterSelectedResponse_IsCopiedAndReleasesSupersededRoot(bool observer, bool invokeRoot)
    {
        var counts = new Counts();
        var original = new Payload { Values = [17, 23, 41] };
        var replacement = new Payload { Values = [5, 8, 13] };
        var filter = new CallbackFilter(async context =>
        {
            if (invokeRoot) await context.Invoke();
            context.Response = CountedResponse.Rent(replacement, counts,
                Assert.IsAssignableFrom<LegacyRequest>(context.Request).Codec);
        });
        await using var fixture = new SendFixture(counts, filter);
        var request = new DirectRequest(original, counts);

        using var response = await fixture.Invoke(request, observer);

        Assert.Null(response.Exception);
        Assert.Equal(invokeRoot ? 2 : 1, counts.PayloadCopies);
        Assert.Equal(1, counts.ResponseCopies);
        Assert.Equal(invokeRoot ? 3 : 2, counts.Rents);
        Assert.Equal(invokeRoot ? 2 : 1, counts.Returns);
        Assert.NotSame(replacement, response.Result);
        replacement.Values.Clear();
        Assert.Equal(new[] { 5, 8, 13 }, Assert.IsType<Payload>(response.Result).Values);
        response.Dispose();
        Assert.Equal(counts.Rents, counts.Returns);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilterRestoresEarlierRoot_IsolatesSelectedResultAndDisposesOtherRoot(bool observer)
    {
        var counts = new Counts();
        var filter = new CallbackFilter(async context =>
        {
            await context.Invoke();
            var first = context.Response;
            await context.Invoke();
            Assert.NotSame(first, context.Response);
            context.Response = first;
        });
        await using var fixture = new SendFixture(counts, filter);
        using var response = await fixture.Invoke(new DirectRequest(new Payload { Values = [17] }, counts), observer);
        Assert.Null(response.Exception);
        Assert.Equal(3, counts.PayloadCopies);
        Assert.Equal(1, counts.ResponseCopies);
        Assert.Equal(3, counts.Rents);
        Assert.Equal(2, counts.Returns);
        response.Dispose();
        Assert.Equal(counts.Rents, counts.Returns);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilterThrowsAfterCopy_ReturnsRootAndPreservesException(bool observer)
    {
        var counts = new Counts();
        var failure = new InvalidOperationException("after isolated invocation");
        var filter = new CallbackFilter(async context =>
        {
            await context.Invoke();
            throw failure;
        });
        await using var fixture = new SendFixture(counts, filter);
        using var response = await fixture.Invoke(new DirectRequest(new Payload { Values = [17] }, counts), observer);
        Assert.Same(failure, response.Exception);
        Assert.Equal(1, counts.PayloadCopies);
        Assert.Equal(1, counts.Rents);
        Assert.Equal(1, counts.Returns);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LegacyCopyFailure_ReturnsOriginalOwnedWrapper(bool observer, bool filtered)
    {
        var counts = new Counts { ThrowCopy = true };
        var filter = filtered ? new CallbackFilter(context => context.Invoke()) : null;
        await using var fixture = new SendFixture(counts, filter);
        using var response = await fixture.Invoke(new LegacyRequest(new Payload { Values = [17] }, counts), observer);
        Assert.Same(counts.CopyFailure, response.Exception);
        Assert.Equal(1, counts.PayloadCopies);
        Assert.Equal(1, counts.Rents);
        Assert.Equal(1, counts.Returns);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CustomCopierReturnsInput_PreservesSelectionAndTransfersOneOwner(bool observer, bool filtered)
    {
        var counts = new Counts { ReturnInput = true };
        var filter = filtered ? new CallbackFilter(context => context.Invoke()) : null;
        await using var fixture = new SendFixture(counts, filter);
        var request = new LegacyRequest(new Payload { Values = [17] }, counts);
        using var response = await fixture.Invoke(request, observer);
        Assert.Null(response.Exception);
        Assert.Same(request.ReturnedResponse, response);
        Assert.Equal(filtered ? 2 : 1, counts.ResponseCopies);
        Assert.Equal(0, counts.PayloadCopies);
        Assert.Equal(1, counts.Rents);
        Assert.Equal(0, counts.Returns);
        response.Dispose();
        Assert.Equal(1, counts.Returns);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task UnsentResponse_ReturnsOwnedHolderWithoutSecondCopy(bool observer, bool filtered, bool expired)
    {
        var counts = new Counts();
        var filter = filtered ? new CallbackFilter(context => context.Invoke()) : null;
        await using var fixture = new SendFixture(counts, filter);
        await fixture.Execute(new DirectRequest(new Payload { Values = [17] }, counts), observer,
            expired ? Message.Directions.Request : Message.Directions.OneWay, expireAfterInvocation: expired);
        Assert.False(fixture.HasResponse);
        Assert.Equal(1, counts.PayloadCopies);
        Assert.Equal(1, counts.Rents);
        Assert.Equal(1, counts.Returns);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedHandoff_ReturnsCurrentHolderAndSurfacesFailure(bool observer)
    {
        var counts = new Counts();
        var failure = new InvalidOperationException("response handoff failed");
        var marker = new SendFailureMarker(failure);
        var filter = new CallbackFilter(context => context.Invoke());
        await using var fixture = new SendFixture(counts, filter)
        {
            HandoffFailure = observer ? failure : null,
            InitialContext = observer ? null : new Dictionary<string, object> { ["response-handoff-failure"] = marker },
        };
        using var response = await fixture.Invoke(new DirectRequest(new Payload { Values = [17] }, counts), observer);
        Assert.Same(failure, response.Exception);
        Assert.Equal(2, counts.PayloadCopies);
        Assert.Equal(2, counts.Rents);
        Assert.Equal(2, counts.Returns);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementCopyFailure_ReturnsReplacementAndSupersededRoot(bool observer)
    {
        var counts = new Counts();
        var filter = new CallbackFilter(async context =>
        {
            await context.Invoke();
            context.Response = CountedResponse.Rent(new Payload { Values = [47] }, counts,
                Assert.IsAssignableFrom<LegacyRequest>(context.Request).Codec);
            counts.ThrowCopy = true;
        });
        await using var fixture = new SendFixture(counts, filter);
        using var response = await fixture.Invoke(new DirectRequest(new Payload { Values = [17] }, counts), observer);
        Assert.Same(counts.CopyFailure, response.Exception);
        Assert.Equal(2, counts.PayloadCopies);
        Assert.Equal(2, counts.Rents);
        Assert.Equal(2, counts.Returns);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FilterReplacesThenThrows_ReturnsEveryOwnedWrapper(bool observer, bool invokeRoot)
    {
        var counts = new Counts();
        var failure = new InvalidOperationException("after replacing response");
        var filter = new CallbackFilter(async context =>
        {
            if (invokeRoot) await context.Invoke();
            context.Response = CountedResponse.Rent(new Payload { Values = [47] }, counts,
                Assert.IsAssignableFrom<LegacyRequest>(context.Request).Codec);
            throw failure;
        });
        await using var fixture = new SendFixture(counts, filter);
        using var response = await fixture.Invoke(new DirectRequest(new Payload { Values = [17] }, counts), observer);
        Assert.Same(failure, response.Exception);
        Assert.Equal(invokeRoot ? 1 : 0, counts.PayloadCopies);
        Assert.Equal(invokeRoot ? 2 : 1, counts.Rents);
        Assert.Equal(counts.Rents, counts.Returns);
    }

    private sealed class SendFailureMarker(Exception failure)
    {
        public Exception Failure { get; } = failure;
        public bool HasFailed { get; set; }
    }

    private sealed class SendFailureCopier : IDeepCopier<SendFailureMarker>
    {
        [return: NotNullIfNotNull(nameof(input))]
        public SendFailureMarker? DeepCopy(SendFailureMarker? input, CopyContext context)
        {
            if (input is null) return null;
            if (!input.HasFailed)
            {
                input.HasFailed = true;
                throw input.Failure;
            }

            return input;
        }
    }

    [GenerateSerializer]
    public sealed class Payload
    {
        [Id(0)]
        public List<int> Values { get; set; } = [];
    }

    private sealed class Counts
    {
        public int PayloadCopies;
        public int ResponseCopies;
        public int Rents;
        public int Returns;
        public bool ThrowCopy;
        public bool ReturnInput;
        public Exception CopyFailure { get; } = new InvalidOperationException("payload copy failed");
        public Response<List<int>>? FallbackOriginal;
        public bool OriginalWasPooledAtSend;
        public bool OriginalWasReturnedOnce;

        public Payload Copy(Payload source)
        {
            PayloadCopies++;
            if (ThrowCopy) throw CopyFailure;
            return new Payload { Values = [.. source.Values] };
        }
    }

    private sealed class CountedResponse : Response, IRawResponseWriter
    {
        private Counts? _counts;
        private IFieldCodec<Payload> _codec = null!;
        private Payload _value = null!;

        public CountedResponse() { }

        public static CountedResponse Rent(Payload value, Counts counts, IFieldCodec<Payload> codec)
        {
            var response = ResponsePool.GetGenerated<CountedResponse>();
            response._value = value;
            response._counts = counts;
            response._codec = codec;
            counts.Rents++;
            return response;
        }

        public override object? Result { get => _value; set => _value = (Payload)value!; }
        public override Exception? Exception { get => null; set => throw new NotSupportedException(); }
        public override Type GetSimpleResultType() => typeof(Payload);
        public override T GetResult<T>() => (T)(object)_value;

        public void WriteRaw<TBufferWriter>(ref Writer<TBufferWriter> writer) where TBufferWriter : IBufferWriter<byte>
        {
            writer.WriteStartObject(0, null!, typeof(Payload));
            _codec.WriteField(ref writer, 0, typeof(Payload), _value);
            writer.WriteEndObject();
        }

        public override void Dispose()
        {
            if (_counts is null) return;
            _counts.Returns++;
            _counts = null;
            _codec = null!;
            _value = null!;
            ResponsePool.ReturnGenerated(this);
        }
    }

    private class LegacyRequest(Payload payload, Counts counts) : IInvokable
    {
        protected ICodecProvider Provider = null!;
        protected Payload Payload => payload;
        protected Counts Counters => counts;
        public IFieldCodec<Payload> Codec => Provider.GetCodec<Payload>();
        public Response? ReturnedResponse { get; protected set; }
        public Action? AfterInvocation { get; set; }
        public object GetTarget() => payload;
        public void SetTarget(ITargetHolder holder) { }
        public int GetArgumentCount() => 0;
        public object? GetArgument(int index) => throw new ArgumentOutOfRangeException(nameof(index));
        public void SetArgument(int index, object value) => throw new ArgumentOutOfRangeException(nameof(index));
        public string GetMethodName() => nameof(ToString);
        public string GetInterfaceName() => nameof(IGrainObserver);
        public string GetActivityName() => nameof(ToString);
        public MethodInfo GetMethod() => typeof(object).GetMethod(nameof(ToString), Type.EmptyTypes)!;
        public Type GetInterfaceType() => typeof(IGrainObserver);
        public void Dispose() { }
        public void Bind(ICodecProvider provider) => Provider = provider;

        public virtual ValueTask<Response> Invoke()
        {
            ReturnedResponse = CountedResponse.Rent(payload, counts, Provider.GetCodec<Payload>());
            AfterInvocation?.Invoke();
            return ValueTask.FromResult(ReturnedResponse);
        }
    }

    private sealed class DirectRequest(Payload payload, Counts counts) : LegacyRequest(payload, counts), IResponseInvokable
    {
        public override ValueTask<Response> Invoke() => throw new InvalidOperationException("The direct request uses InvokeAndCopy.");

        public ValueTask<Response> InvokeAndCopy(ICodecProvider provider, CopyContextPool contexts, DeepCopier<Response> responseCopier)
        {
            ReturnedResponse = CountedResponse.Rent(Counters.Copy(Payload), Counters, provider.GetCodec<Payload>());
            AfterInvocation?.Invoke();
            return ValueTask.FromResult(ReturnedResponse);
        }
    }

    private sealed class CountingResponseCopier(Counts counts, ICodecProvider provider) : IDeepCopier<Response>
    {
        [return: NotNullIfNotNull(nameof(input))]
        public Response? DeepCopy(Response? input, CopyContext context)
        {
            if (input is null) return null;
            if (input is Response<List<int>> list) return CopyListResponse(list, counts);
            counts.ResponseCopies++;
            if (counts.ReturnInput) return input;
            if (input.Exception is not null) return input;
            return CountedResponse.Rent(counts.Copy(Assert.IsType<Payload>(input.Result)), counts, provider.GetCodec<Payload>());
        }
    }

    private static Response<List<int>> CopyListResponse(Response<List<int>> input, Counts counts)
    {
        counts.ResponseCopies++;
        counts.FallbackOriginal = input;
        if (counts.ReturnInput) return input;
        counts.PayloadCopies++;
        if (counts.ThrowCopy) throw counts.CopyFailure;
        return (Response<List<int>>)Response.FromResult(new List<int>(input.TypedResult!));
    }

    private sealed class FallbackListCopier(Counts counts) : IDeepCopier<Response<List<int>>>
    {
        [return: NotNullIfNotNull(nameof(input))]
        public Response<List<int>>? DeepCopy(Response<List<int>>? input, CopyContext context)
            => input is null ? null : CopyListResponse(input, counts);
    }

    private sealed class UnusedResponseCodec : IFieldCodec<Response>
    {
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type? expectedType, Response? value)
            where TBufferWriter : IBufferWriter<byte> => throw new NotSupportedException();
        public Response ReadValue<TInput>(ref Reader<TInput> reader, Orleans.Serialization.WireProtocol.Field field) => throw new NotSupportedException();
    }

    private sealed class CountingHolderCopier(Counts counts, ICodecProvider provider) : IDeepCopier<CountedResponse>
    {
        [return: NotNullIfNotNull(nameof(input))]
        public CountedResponse? DeepCopy(CountedResponse? input, CopyContext context)
        {
            if (input is null) return null;
            counts.ResponseCopies++;
            return CountedResponse.Rent(counts.Copy(Assert.IsType<Payload>(input.Result)), counts, provider.GetCodec<Payload>());
        }
    }

    private sealed class UnusedHolderCodec : IFieldCodec<CountedResponse>
    {
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type? expectedType, CountedResponse? value)
            where TBufferWriter : IBufferWriter<byte> => throw new NotSupportedException();
        public CountedResponse ReadValue<TInput>(ref Reader<TInput> reader, Orleans.Serialization.WireProtocol.Field field) => throw new NotSupportedException();
    }

    private sealed class CallbackFilter(Func<IIncomingGrainCallContext, Task> callback) : IIncomingGrainCallFilter
    {
        public Task Invoke(IIncomingGrainCallContext context) => callback(context);
    }

    private sealed class Observer : IGrainObserver
    {
    }

    private sealed class SendFixture : IAsyncDisposable, IResponseCompletionSource
    {
        private readonly ServiceProvider _services;
        private readonly FakeTimeProvider _clock = new();
        private readonly InsideRuntimeClient _runtime;
        private readonly HostedClient _hosted;
        private readonly InvokableObjectManager _manager;
        private readonly IAddressable _observer;
        private readonly Counts _counts;
        private readonly ObserverGrainId _observerId = ObserverGrainId.Create(ClientGrainId.Create("response-copy"), IdSpan.Create("observer"));
        private readonly TaskCompletionSource<Response> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SharedMemoryPool _memory = new();
        private readonly MessageSerializer _serializer;
        private Message _request = null!;
        private Response? _sentResponse;
        public bool HasResponse => _sentResponse is not null;
        public Exception? HandoffFailure { get; set; }
        public Dictionary<string, object>? InitialContext { get; set; }
        public CodecProvider Provider => _services.GetRequiredService<CodecProvider>();

        public SendFixture(Counts counts, IIncomingGrainCallFilter? filter, IAddressable? target = null)
        {
            _counts = counts;
            _observer = target ?? new Observer();
            var services = new ServiceCollection();
            services.AddSerializer(builder => builder.Configure(options =>
            {
                options.AddSerializer<Response>(_ => new UnusedResponseCodec(), provider => new CountingResponseCopier(counts, provider));
                options.AddSerializer<CountedResponse>(_ => new UnusedHolderCodec(), provider => new CountingHolderCopier(counts, provider));
                options.AddCopier(typeof(SendFailureCopier));
                options.AddSerializer<Response<List<int>>>(
                    provider => new PooledResponseCodec<List<int>, IFieldCodec<List<int>>>(provider.GetCodec<List<int>>()),
                    _ => new FallbackListCopier(counts));
            }));
            services.AddLogging();
            services.AddMetrics();
            if (filter is not null) services.AddSingleton(filter);
            _services = services.BuildServiceProvider();
            var copier = _services.GetRequiredService<DeepCopier>();
            var instruments = new OrleansInstruments(_services.GetRequiredService<IMeterFactory>());
            var messaging = new MessagingInstruments(instruments);
            var processing = new MessagingProcessingInstruments(instruments);
            var trace = new MessagingTrace(NullLoggerFactory.Instance, messaging, processing);
            var factory = new MessageFactory(copier, NullLogger<MessageFactory>.Instance, trace);
            var options = Options.Create(new SiloMessagingOptions());
            var mapping = new InterfaceToImplementationMappingCache();
            var referenceRuntime = Substitute.For<IGrainReferenceRuntime>();
            var activator = new GrainReferenceActivator(_services, [new UntypedReferenceProvider(_services, referenceRuntime)]);
            var silo = Substitute.For<ILocalSiloDetails>();
            silo.SiloAddress.Returns(SiloAddress.New(IPAddress.Loopback, 0, 1));
            silo.GatewayAddress.Returns((SiloAddress)null!);
            _runtime = new InsideRuntimeClient(silo, _services, factory, NullLoggerFactory.Instance, options, trace, activator,
                new GrainInterfaceTypeResolver([], _services.GetRequiredService<Orleans.Serialization.TypeSystem.TypeConverter>()),
                new GrainInterfaceTypeToGrainTypeResolver(Substitute.For<IClusterManifestProvider>()),
                copier, _clock, mapping, instruments);
            var messageCenter = new MessageCenter(silo, factory, null!,
                _ => throw new InvalidOperationException("The response fixture has no gateway."),
                NullLogger<MessageCenter>.Instance, Substitute.For<ISiloStatusOracle>(), null!,
                new RuntimeMessagingTrace(NullLoggerFactory.Instance, messaging, processing), messaging, processing,
                options, null!, null!, new NoOpMessageStatisticsSink());
            _hosted = new HostedClient(_runtime, silo, NullLogger<HostedClient>.Instance, referenceRuntime,
                Substitute.For<IInternalGrainFactory>(), messageCenter, trace, copier, activator, mapping);
            messageCenter.SetHostedClient(_hosted);
            typeof(InsideRuntimeClient).GetField("messageCenter", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_runtime, messageCenter);
            typeof(InsideRuntimeClient).GetField("grainCallFilters", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_runtime,
                _services.GetServices<IIncomingGrainCallFilter>().ToList());
            var observerRuntime = Substitute.For<IRuntimeClient>();
            observerRuntime.ServiceProvider.Returns(_services);
            observerRuntime.When(client => client.SendResponse(Arg.Any<Message>(), Arg.Any<Response>()))
                .Do(call =>
                {
                    if (call.Arg<Response>() is CountedResponse && HandoffFailure is { } failure)
                    {
                        HandoffFailure = null;
                        throw failure;
                    }

                    ((IResponseCompletionSource)this).Complete(call.Arg<Response>());
                });
            _manager = new InvokableObjectManager(Substitute.For<IGrainContext>(), observerRuntime, copier, trace,
                _services.GetRequiredService<DeepCopier<Response>>(), mapping, NullLogger<InvokableObjectManager>.Instance);
            Assert.True(_manager.TryRegister(_observer, _observerId));
            _serializer = new MessageSerializer(_services.GetRequiredService<SerializerSessionPool>(), _memory, options.Value);
        }

        public async Task<Response> Invoke(IInvokable request, bool observer)
        {
            await Execute(request, observer);
            var response = await _completion.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            _sentResponse = null;
            return response;
        }

        public async Task Execute(IInvokable request, bool observer,
            Message.Directions direction = Message.Directions.Request, bool expireAfterInvocation = false)
        {
            if (request is LegacyRequest legacy) legacy.Bind(_services.GetRequiredService<ICodecProvider>());
            _request = new Message
            {
                Id = new CorrelationId(7123),
                Direction = direction,
                SendingGrain = _hosted.GrainId,
                SendingSilo = _hosted.Address.SiloAddress,
                TargetGrain = observer ? _observerId.GrainId : GrainId.Create("response-copy", "target"),
                TargetSilo = _hosted.Address.SiloAddress,
                BodyObject = request,
                RequestContextData = InitialContext,
            };
            if (expireAfterInvocation)
                Assert.IsAssignableFrom<LegacyRequest>(request).AfterInvocation = () => _request.TimeToLive = TimeSpan.FromMilliseconds(-1);
            if (observer)
            {
                _manager.Dispatch(_request);
                await _manager.StopAsync().WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            }
            else
            {
                var callbacks = (ConcurrentDictionary<(GrainId, CorrelationId), CallbackData>)typeof(InsideRuntimeClient)
                    .GetField("callbacks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_runtime)!;
                var shared = new SharedCallbackData(message => callbacks.TryRemove((_request.SendingGrain, _request.Id), out _),
                    NullLogger<CallbackData>.Instance, _clock, TimeSpan.FromMinutes(1), false, false, null!);
                Assert.True(callbacks.TryAdd((_request.SendingGrain, _request.Id),
                    new CallbackData(shared, this, _request, new ApplicationRequestInstruments(
                        new OrleansInstruments(_services.GetRequiredService<IMeterFactory>())))));
                var target = Substitute.For<IGrainContext>();
                target.GrainInstance.Returns(_observer);
                target.GetTarget().Returns(_observer);
                target.GrainId.Returns(_request.TargetGrain);
                target.ActivationServices.Returns(_services);
                await _runtime.Invoke(target, _request);
            }
        }

        public async Task AssertFrameRoundTrip(Response response)
        {
            var message = new Message
            {
                Direction = Message.Directions.Response,
                Id = _request.Id,
                SendingGrain = _request.TargetGrain,
                TargetGrain = _request.SendingGrain,
                BodyObject = response,
            };
            var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
            try
            {
                var written = _serializer.Write(pipe.Writer, message);
                await pipe.Writer.FlushAsync(TestContext.Current.CancellationToken);
                Assert.True(pipe.Reader.TryRead(out var read));
                var bytes = read.Buffer.ToArray();
                pipe.Reader.AdvanceTo(read.Buffer.End);
                var input = new ReadOnlySequence<byte>(bytes);
                var (required, headers, body) = _serializer.TryRead(ref input, out var received);
                Assert.Equal(0, required);
                Assert.Equal(written.HeaderLength, headers);
                Assert.Equal(written.BodyLength, body);
                Assert.True(input.IsEmpty);
                Assert.NotNull(received);
                Assert.Equal(message.Direction, received.Direction);
                Assert.Equal(message.Id, received.Id);
                using var result = Assert.IsAssignableFrom<Response>(received.BodyObject);
                Assert.Equal(Assert.IsType<Payload>(response.Result).Values, Assert.IsType<Payload>(result.Result).Values);
            }
            finally
            {
                await pipe.Writer.CompleteAsync();
                await pipe.Reader.CompleteAsync();
            }
        }

        void IResponseCompletionSource.Complete(Response value)
        {
            Assert.Null(_sentResponse);
            if (_counts.FallbackOriginal is { } original && (!_counts.ReturnInput || _counts.ThrowCopy))
            {
                var first = ResponsePool.Get<List<int>>();
                var second = ResponsePool.Get<List<int>>();
                _counts.OriginalWasPooledAtSend = ReferenceEquals(original, first);
                _counts.OriginalWasReturnedOnce = !ReferenceEquals(original, second);
                second.Dispose();
                first.Dispose();
            }

            _sentResponse = value;
            _completion.TrySetResult(value);
        }

        void IResponseCompletionSource.Complete() => ((IResponseCompletionSource)this).Complete(Response.Completed);

        public async ValueTask DisposeAsync()
        {
            await _manager.StopAsync();
            _sentResponse?.Dispose();
            ((IDisposable)_hosted).Dispose();
            _serializer.Dispose();
            _memory.Pool.Dispose();
            await _services.DisposeAsync();
            GC.KeepAlive(_observer);
        }
    }

    private sealed class UntypedReferenceProvider(IServiceProvider services, IGrainReferenceRuntime runtime) : IGrainReferenceActivatorProvider
    {
        public bool TryGet(GrainType grainType, GrainInterfaceType interfaceType, [NotNullWhen(true)] out IGrainReferenceActivator? activator)
        {
            activator = new UntypedReferenceActivator(new GrainReferenceShared(grainType, interfaceType, 0, runtime,
                InvokeMethodOptions.None, services.GetRequiredService<CodecProvider>(), services.GetRequiredService<CopyContextPool>(), services));
            return true;
        }

        private sealed class UntypedReferenceActivator(GrainReferenceShared shared) : IGrainReferenceActivator
        {
            public GrainReference CreateReference(GrainId grainId) => GrainReference.FromGrainId(shared, grainId);
        }
    }
}
