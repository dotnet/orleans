#if NET8_0_OR_GREATER
using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.TestKit;
using System.Distributed.DurableTasks;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Serialization.UnitTests;

public sealed class DurableTaskResponseCodecTests(ITestOutputHelper output, SerializationTesterFixture fixture)
    : FieldCodecTester<DurableTaskResponse<int>, IFieldCodec<DurableTaskResponse<int>>>(output, fixture), IClassFixture<SerializationTesterFixture>
{
    protected override IFieldCodec<DurableTaskResponse<int>> CreateCodec() => ServiceProvider.GetRequiredService<ICodecProvider>().GetCodec<DurableTaskResponse<int>>();

    protected override DurableTaskResponse<int> CreateValue() => new(42);

    protected override DurableTaskResponse<int>[] TestValues => [new(0), new(42), null!];

    protected override bool Equals(DurableTaskResponse<int>? left, DurableTaskResponse<int>? right)
        => left?.TypedResult == right?.TypedResult;
}

public sealed class DurableTaskResponseCopierTests(ITestOutputHelper output, SerializationTesterFixture fixture)
    : CopierTester<DurableTaskResponse<int>, IDeepCopier<DurableTaskResponse<int>>>(output, fixture), IClassFixture<SerializationTesterFixture>
{
    protected override DurableTaskResponse<int> CreateValue() => new(42);

    protected override DurableTaskResponse<int>[] TestValues => [new(0), new(42), null!];

    protected override bool Equals(DurableTaskResponse<int>? left, DurableTaskResponse<int>? right)
        => left?.TypedResult == right?.TypedResult;
}

public sealed class ExceptionDurableTaskResponseCodecTests(ITestOutputHelper output, SerializationTesterFixture fixture)
    : FieldCodecTester<ExceptionDurableTaskResponse, IFieldCodec<ExceptionDurableTaskResponse>>(output, fixture), IClassFixture<SerializationTesterFixture>
{
    protected override int[] MaxSegmentSizes => [8096];

    protected override IFieldCodec<ExceptionDurableTaskResponse> CreateCodec() => ServiceProvider.GetRequiredService<ICodecProvider>().GetCodec<ExceptionDurableTaskResponse>();

    protected override ExceptionDurableTaskResponse CreateValue() => new(new InvalidOperationException("Test exception"));

    protected override ExceptionDurableTaskResponse[] TestValues =>
        [new(new InvalidOperationException("Test exception")), new(new OperationCanceledException("Test cancellation")), null!];

    protected override bool Equals(ExceptionDurableTaskResponse? left, ExceptionDurableTaskResponse? right)
        => DurableTaskResponseTestHelpers.ExceptionsEqual(left?.Exception, right?.Exception);
}

public sealed class ExceptionDurableTaskResponseCopierTests(ITestOutputHelper output, SerializationTesterFixture fixture)
    : CopierTester<ExceptionDurableTaskResponse, IDeepCopier<ExceptionDurableTaskResponse>>(output, fixture), IClassFixture<SerializationTesterFixture>
{
    protected override ExceptionDurableTaskResponse CreateValue() => new(new InvalidOperationException("Test exception"));

    protected override ExceptionDurableTaskResponse[] TestValues =>
        [new(new InvalidOperationException("Test exception")), new(new OperationCanceledException("Test cancellation")), null!];

    protected override bool Equals(ExceptionDurableTaskResponse? left, ExceptionDurableTaskResponse? right)
        => DurableTaskResponseTestHelpers.ExceptionsEqual(left?.Exception, right?.Exception);
}

public sealed class PendingDurableTaskResponseCodecTests(ITestOutputHelper output, SerializationTesterFixture fixture)
    : FieldCodecTester<PendingDurableTaskResponse, IFieldCodec<PendingDurableTaskResponse>>(output, fixture), IClassFixture<SerializationTesterFixture>
{
    protected override IFieldCodec<PendingDurableTaskResponse> CreateCodec() => ServiceProvider.GetRequiredService<ICodecProvider>().GetCodec<PendingDurableTaskResponse>();

    protected override PendingDurableTaskResponse CreateValue() => PendingDurableTaskResponse.Instance;

    protected override PendingDurableTaskResponse[] TestValues => [PendingDurableTaskResponse.Instance, null!];

    protected override bool Equals(PendingDurableTaskResponse? left, PendingDurableTaskResponse? right)
        => DurableTaskResponseTestHelpers.SameType(left, right);
}

public sealed class PendingDurableTaskResponseCopierTests(ITestOutputHelper output, SerializationTesterFixture fixture)
    : CopierTester<PendingDurableTaskResponse, IDeepCopier<PendingDurableTaskResponse>>(output, fixture), IClassFixture<SerializationTesterFixture>
{
    protected override bool IsImmutable => true;

    protected override PendingDurableTaskResponse CreateValue() => PendingDurableTaskResponse.Instance;

    protected override PendingDurableTaskResponse[] TestValues => [PendingDurableTaskResponse.Instance, null!];

    protected override bool Equals(PendingDurableTaskResponse? left, PendingDurableTaskResponse? right)
        => DurableTaskResponseTestHelpers.SameType(left, right);
}

public sealed class SubscribedDurableTaskResponseCodecTests(ITestOutputHelper output, SerializationTesterFixture fixture)
    : FieldCodecTester<SubscribedDurableTaskResponse, IFieldCodec<SubscribedDurableTaskResponse>>(output, fixture), IClassFixture<SerializationTesterFixture>
{
    protected override IFieldCodec<SubscribedDurableTaskResponse> CreateCodec() => ServiceProvider.GetRequiredService<ICodecProvider>().GetCodec<SubscribedDurableTaskResponse>();

    protected override SubscribedDurableTaskResponse CreateValue() => SubscribedDurableTaskResponse.Instance;

    protected override SubscribedDurableTaskResponse[] TestValues => [SubscribedDurableTaskResponse.Instance, null!];

    protected override bool Equals(SubscribedDurableTaskResponse? left, SubscribedDurableTaskResponse? right)
        => DurableTaskResponseTestHelpers.SameType(left, right);
}

public sealed class SubscribedDurableTaskResponseCopierTests(ITestOutputHelper output, SerializationTesterFixture fixture)
    : CopierTester<SubscribedDurableTaskResponse, IDeepCopier<SubscribedDurableTaskResponse>>(output, fixture), IClassFixture<SerializationTesterFixture>
{
    protected override bool IsImmutable => true;

    protected override SubscribedDurableTaskResponse CreateValue() => SubscribedDurableTaskResponse.Instance;

    protected override SubscribedDurableTaskResponse[] TestValues => [SubscribedDurableTaskResponse.Instance, null!];

    protected override bool Equals(SubscribedDurableTaskResponse? left, SubscribedDurableTaskResponse? right)
        => DurableTaskResponseTestHelpers.SameType(left, right);
}

public sealed class SuccessDurableTaskResponseCodecTests(ITestOutputHelper output, SerializationTesterFixture fixture)
    : FieldCodecTester<SuccessDurableTaskResponse, IFieldCodec<SuccessDurableTaskResponse>>(output, fixture), IClassFixture<SerializationTesterFixture>
{
    protected override IFieldCodec<SuccessDurableTaskResponse> CreateCodec() => ServiceProvider.GetRequiredService<ICodecProvider>().GetCodec<SuccessDurableTaskResponse>();

    protected override SuccessDurableTaskResponse CreateValue() => SuccessDurableTaskResponse.Instance;

    protected override SuccessDurableTaskResponse[] TestValues => [SuccessDurableTaskResponse.Instance, null!];

    protected override bool Equals(SuccessDurableTaskResponse? left, SuccessDurableTaskResponse? right)
        => DurableTaskResponseTestHelpers.SameType(left, right);
}

public sealed class SuccessDurableTaskResponseCopierTests(ITestOutputHelper output, SerializationTesterFixture fixture)
    : CopierTester<SuccessDurableTaskResponse, IDeepCopier<SuccessDurableTaskResponse>>(output, fixture), IClassFixture<SerializationTesterFixture>
{
    protected override bool IsImmutable => true;

    protected override SuccessDurableTaskResponse CreateValue() => SuccessDurableTaskResponse.Instance;

    protected override SuccessDurableTaskResponse[] TestValues => [SuccessDurableTaskResponse.Instance, null!];

    protected override bool Equals(SuccessDurableTaskResponse? left, SuccessDurableTaskResponse? right)
        => DurableTaskResponseTestHelpers.SameType(left, right);
}

internal static class DurableTaskResponseTestHelpers
{
    public static bool SameType(DurableTaskResponse? left, DurableTaskResponse? right)
        => left is null ? right is null : right is not null && left.GetType() == right.GetType();

    public static bool ExceptionsEqual(Exception? left, Exception? right)
        => left is null ? right is null : right is not null && left.GetType() == right.GetType() && left.Message == right.Message;
}
#endif
