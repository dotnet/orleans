using System.Globalization;
using Amazon.CDK.AWS.SQS;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.AWS;
using Aspire.Hosting.AWS.CDK;
using Aspire.Hosting.Orleans;

namespace Aspire.Hosting;

/// <summary>
/// Represents an Orleans SQS stream provider and its AWS CDK resources.
/// </summary>
public sealed class SqsStreamingResource : IProviderConfiguration
{
    private readonly OrleansService _orleansService;
    private readonly SqsStreamingOptions _options;

    internal SqsStreamingResource(
        OrleansService orleansService,
        string name,
        SqsStreamingOptions options,
        IAWSSDKConfig awsSdkConfig,
        IResourceBuilder<IStackResource> stack,
        IReadOnlyList<IResourceBuilder<IConstructResource<Queue>>> queues)
    {
        _orleansService = orleansService;
        Name = name;
        _options = options;
        AwsSdkConfig = awsSdkConfig;
        Stack = stack;
        Queues = queues;
    }

    /// <summary>
    /// Gets the Orleans stream provider name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the validated configuration used for Orleans and AWS CDK.
    /// </summary>
    public SqsStreamingOptions Options => _options;

    /// <summary>
    /// Gets the AWS SDK configuration associated with the provider.
    /// </summary>
    public IAWSSDKConfig AwsSdkConfig { get; }

    /// <summary>
    /// Gets the AWS CDK stack which owns the partition queues.
    /// </summary>
    public IResourceBuilder<IStackResource> Stack { get; }

    /// <summary>
    /// Gets the AWS CDK queue resources in the provider topology.
    /// </summary>
    public IReadOnlyList<IResourceBuilder<IConstructResource<Queue>>> Queues { get; }

    /// <inheritdoc />
    public void ConfigureResource<T>(IResourceBuilder<T> resourceBuilder, string configSectionPath)
        where T : IResourceWithEnvironment
    {
        var prefix = $"Orleans__{configSectionPath.Replace(":", "__", StringComparison.Ordinal)}";
        resourceBuilder
            .WithReference(AwsSdkConfig)
            .WithEnvironment($"{prefix}__ProviderType", "SQS")
            .WithEnvironment($"{prefix}__Region", AwsSdkConfig.Region!.SystemName)
            .WithEnvironment($"{prefix}__PartitionCount", _options.PartitionCount.ToString(CultureInfo.InvariantCulture))
            .WithEnvironment($"{prefix}__FifoQueue", _options.FifoQueue.ToString());
        resourceBuilder.WithEnvironment(context =>
        {
            OrleansSqsStreamingExtensions.ValidateServiceId(
                _orleansService,
                _options.ServiceId,
                allowUnset: false);
        });
        if (resourceBuilder.Resource is IResourceWithWaitSupport)
        {
            resourceBuilder.WithAnnotation(
                new WaitAnnotation(Stack.Resource, WaitType.WaitUntilHealthy, exitCode: 0));
        }

        AddOptionalValue(resourceBuilder, prefix, nameof(_options.ReceiveWaitTimeSeconds), _options.ReceiveWaitTimeSeconds);
        AddOptionalValue(resourceBuilder, prefix, nameof(_options.VisibilityTimeoutSeconds), _options.VisibilityTimeoutSeconds);
        AddOptionalValue(resourceBuilder, prefix, nameof(_options.CacheSize), _options.CacheSize);
        AddOptionalValue(resourceBuilder, prefix, nameof(_options.DataAdapterKey), _options.DataAdapterKey);
        AddValues(resourceBuilder, prefix, nameof(_options.ReceiveMessageAttributes), _options.ReceiveMessageAttributes);
        AddValues(resourceBuilder, prefix, nameof(_options.ReceiveMessageSystemAttributes), _options.ReceiveMessageSystemAttributes);
    }

    private static void AddOptionalValue<T>(
        IResourceBuilder<T> resourceBuilder,
        string prefix,
        string name,
        int? value)
        where T : IResourceWithEnvironment
    {
        if (value is { } configuredValue)
        {
            resourceBuilder.WithEnvironment(
                $"{prefix}__{name}",
                configuredValue.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void AddOptionalValue<T>(
        IResourceBuilder<T> resourceBuilder,
        string prefix,
        string name,
        string? value)
        where T : IResourceWithEnvironment
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            resourceBuilder.WithEnvironment($"{prefix}__{name}", value);
        }
    }

    private static void AddValues<T>(
        IResourceBuilder<T> resourceBuilder,
        string prefix,
        string name,
        IReadOnlyList<string> values)
        where T : IResourceWithEnvironment
    {
        for (var index = 0; index < values.Count; index++)
        {
            resourceBuilder.WithEnvironment($"{prefix}__{name}__{index}", values[index]);
        }
    }
}
