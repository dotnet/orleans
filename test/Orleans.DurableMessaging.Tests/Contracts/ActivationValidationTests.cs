using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Orleans.Concurrency;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.DurableMessaging.Tests.Functional;
using Orleans.Journaling;
using Orleans.Metadata;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Contracts;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class ActivationValidationTests
{
    private static readonly Action<IGrainContext, GrainProperties, PlacementStrategy> ValidateActivation = ReceiverTestServices
        .GetImplementationType("DurableMessagingActivationValidator")
        .GetMethod("Validate")!
        .CreateDelegate<Action<IGrainContext, GrainProperties, PlacementStrategy>>();

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

        var values = new Dictionary<string, string>();
        new AttributeGrainPropertiesProvider(Substitute.For<IServiceProvider>())
            .Populate(grain.GetType(), GrainType.Create("activation-validation"), values);
        var properties = new GrainProperties(values.ToImmutableDictionary(StringComparer.Ordinal));
        var placement = model == "stateless"
            ? new StatelessWorkerAttribute().PlacementStrategy
            : new RandomPlacementAttribute().PlacementStrategy;
        var exception = Assert.Throws<InvalidOperationException>(() => ValidateActivation(context, properties, placement));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
        Assert.Contains(grain.GetType().ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingGrainInstance_FailsBeforeExecutionModelValidation()
    {
        var context = Substitute.For<IGrainContext>();

        var properties = new GrainProperties(ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal));
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ValidateActivation(context, properties, new RandomPlacementAttribute().PlacementStrategy));

        Assert.Contains("initialized grain instance", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StandardStateConstruction_MissingSelectedCodecFailsBeforeEnrollment()
    {
        const string selectedFormat = "orleans-binary";
        var builder = InboxStateManagerBoundaryTests.CreateBuilder(selectedFormat);
        builder.Services.RemoveAllKeyed(typeof(IDurableValueCommandCodec<>), selectedFormat);
        var id = new JournalId("missing-codec/" + Guid.NewGuid().ToString("N"));
        builder.Services.AddScoped<IJournaledStateManager>(sp =>
            sp.GetRequiredService<IJournaledStateManagerFactory>().CreateStandalone(id));
        await using var provider = builder.Services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        var owner = scope.ServiceProvider.GetRequiredService<IJournaledStateManager>();
        var exception = Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredKeyedService<IDurableValue<int>>("state"));
        Assert.Contains(selectedFormat, exception.Message, StringComparison.Ordinal);
        Assert.False(owner.TryGetStateMachine("state", out _));
    }

    [Fact]
    public async Task CodecResolution_PreservesUnrelatedFailure()
    {
        var cause = new InvalidOperationException("codec resolution failure");
        const string selectedFormat = "orleans-binary";
        var builder = InboxStateManagerBoundaryTests.CreateBuilder(selectedFormat);
        builder.Services.AddKeyedSingleton<IDurableDictionaryCommandCodec<string, int>>(selectedFormat, (_, _) => throw cause);
        var id = new JournalId("codec-resolution/" + Guid.NewGuid().ToString("N"));
        builder.Services.AddScoped<IJournaledStateManager>(sp =>
            sp.GetRequiredService<IJournaledStateManagerFactory>().CreateStandalone(id));
        await using var provider = builder.Services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        var owner = scope.ServiceProvider.GetRequiredService<IJournaledStateManager>();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredKeyedService<IDurableDictionary<string, int>>("state"));

        Assert.Same(cause, exception);
        Assert.False(owner.TryGetStateMachine("state", out _));
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
