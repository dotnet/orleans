using NSubstitute;
using Orleans.Concurrency;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Contracts;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class ActivationValidationTests
{
    private static readonly Action<IGrainContext> ValidateActivation = ReceiverTestServices
        .GetImplementationType("DurableMessagingActivationValidator")
        .GetMethod("Validate")!
        .CreateDelegate<Action<IGrainContext>>();

    [Fact]
    public void ExternalConsumerAssembly_HasNoFriendAccessToDurableMessaging()
    {
        var sourceAssembly = typeof(IDurableInbox).Assembly;
        var consumerName = typeof(ActivationValidationTests).Assembly.GetName().Name;
        var friendDeclarations = sourceAssembly
            .GetCustomAttributesData()
            .Where(attribute => attribute.AttributeType.FullName == "System.Runtime.CompilerServices.InternalsVisibleToAttribute")
            .Select(attribute => attribute.ConstructorArguments[0].Value?.ToString())
            .ToArray();

        Assert.DoesNotContain(friendDeclarations, declaration =>
            declaration?.StartsWith(consumerName!, StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("reentrant", "non-reentrant")]
    [InlineData("stateless", "one activation")]
    [InlineData("interleavable-method", "interleavable method")]
    [InlineData("may-interleave", "non-reentrant")]
    public void UnsupportedExecutionModel_FailsWithSpecificDiagnostic(string model, string expected)
    {
        object grain = model switch
        {
            "reentrant" => new ReentrantGrain(),
            "stateless" => new StatelessGrain(),
            "interleavable-method" => new InterleavableGrain(),
            "may-interleave" => new MayInterleaveGrain(),
            _ => throw new ArgumentOutOfRangeException(nameof(model))
        };
        var context = Substitute.For<IGrainContext>();
        context.GrainInstance.Returns(grain);

        var exception = Assert.Throws<InvalidOperationException>(() => ValidateActivation(context));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
        Assert.Contains(grain.GetType().ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingGrainInstance_FailsBeforeExecutionModelValidation()
    {
        var context = Substitute.For<IGrainContext>();

        var exception = Assert.Throws<InvalidOperationException>(() => ValidateActivation(context));

        Assert.Contains("initialized grain instance", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingCodecCapability_PreservesRequiredContractFailure()
    {
        var manager = Substitute.For<IJournaledStateManager>();
        var cause = new NotSupportedException("manager-bound codec capability");
        manager.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<string, int>>().Returns(_ => throw cause);

        var exception = Assert.Throws<NotSupportedException>(() =>
            ReceiverTestServices.CreateDeferredDictionary<string, int>(manager));

        Assert.Same(cause, exception);
    }

    [Fact]
    public void CodecResolution_PreservesUnrelatedFailure()
    {
        var manager = Substitute.For<IJournaledStateManager>();
        var cause = new InvalidOperationException("codec resolution failure");
        manager.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<string, int>>().Returns(_ => throw cause);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ReceiverTestServices.CreateDeferredDictionary<string, int>(manager));

        Assert.Same(cause, exception);
    }

    [Reentrant]
    private sealed class ReentrantGrain;

    [StatelessWorker]
    private sealed class StatelessGrain;

    [MayInterleave(nameof(Interleave))]
    private sealed class MayInterleaveGrain
    {
        public static bool Interleave(IInvokable request) => true;
    }

    public interface IInterleavableBase
    {
        [AlwaysInterleave]
        Task PingAsync();
    }

    public interface IInterleavableGrain : IGrain, IInterleavableBase;

    private sealed class InterleavableGrain : IInterleavableGrain
    {
        public Task PingAsync() => Task.CompletedTask;
    }
}
