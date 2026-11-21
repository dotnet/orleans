using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Durable.Journaling;
using Orleans.Durable.Messaging.Tests.Functional;
using Xunit;

namespace Orleans.Durable.Messaging.Tests.Contracts;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class ActivationValidationTests
{
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
}
