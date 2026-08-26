using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Amazon.CDK;
using Amazon.CDK.AWS.SQS;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.AWS;
using Aspire.Hosting.AWS.CDK;
using Aspire.Hosting.Orleans;
using OrleansAWSUtils.Storage;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for configuring Orleans SQS streaming in .NET Aspire.
/// </summary>
public static partial class OrleansSqsStreamingExtensions
{
    /// <summary>
    /// Adds an AWS CDK-provisioned SQS stream provider to an Orleans service.
    /// </summary>
    /// <param name="orleansService">The Orleans service.</param>
    /// <param name="name">The stream provider name.</param>
    /// <param name="awsSdkConfig">The AWS SDK profile and region configuration.</param>
    /// <param name="options">The SQS topology and runtime options.</param>
    /// <returns>The Orleans service.</returns>
    public static OrleansService WithSqsStreaming(
        this OrleansService orleansService,
        string name,
        IAWSSDKConfig awsSdkConfig,
        SqsStreamingOptions options)
    {
        AddSqsStreaming(orleansService, name, awsSdkConfig, options);
        return orleansService;
    }

    /// <summary>
    /// Adds an AWS CDK-provisioned SQS stream provider to an Orleans service and returns its resource model.
    /// </summary>
    /// <param name="orleansService">The Orleans service.</param>
    /// <param name="name">The stream provider name.</param>
    /// <param name="awsSdkConfig">The AWS SDK profile and region configuration.</param>
    /// <param name="options">The SQS topology and runtime options.</param>
    /// <returns>The SQS stream provider resource.</returns>
    public static SqsStreamingResource AddSqsStreaming(
        this OrleansService orleansService,
        string name,
        IAWSSDKConfig awsSdkConfig,
        SqsStreamingOptions options)
    {
        ArgumentNullException.ThrowIfNull(orleansService);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(awsSdkConfig);
        ArgumentNullException.ThrowIfNull(options);

        var validatedOptions = ValidateAndCopy(name, awsSdkConfig, options);
        ValidateServiceId(orleansService, validatedOptions.ServiceId, allowUnset: true);

        var resourceName = NormalizeResourceName($"{orleansService.Name}-{name}-sqs");
        var stack = orleansService.Builder.AddAWSCDKStack(resourceName).WithReference(awsSdkConfig);
        var queues = CreateQueues(stack, name, validatedOptions);
        var resource = new SqsStreamingResource(
            orleansService,
            name,
            validatedOptions,
            awsSdkConfig,
            stack,
            queues);

        orleansService
            .WithServiceId(validatedOptions.ServiceId)
            .WithStreaming(name, resource);
        return resource;
    }

    private static SqsStreamingOptions ValidateAndCopy(
        string providerName,
        IAWSSDKConfig awsSdkConfig,
        SqsStreamingOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ServiceId);
        if (awsSdkConfig.Region is null)
        {
            throw new ArgumentException("SQS streaming requires a concrete AWS region.", nameof(awsSdkConfig));
        }

        if (options.PartitionCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "PartitionCount must be greater than zero.");
        }

        ValidateRange(options.ReceiveWaitTimeSeconds, 0, 20, nameof(options.ReceiveWaitTimeSeconds));
        ValidateRange(options.VisibilityTimeoutSeconds, 0, 43_200, nameof(options.VisibilityTimeoutSeconds));
        if (options.CacheSize is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "CacheSize must be greater than zero.");
        }

        if (string.Equals(options.DataAdapterKey, providerName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "DataAdapterKey must differ from the stream provider name.",
                nameof(options));
        }

        var result = new SqsStreamingOptions
        {
            ServiceId = options.ServiceId,
            PartitionCount = options.PartitionCount,
            FifoQueue = options.FifoQueue,
            ReceiveWaitTimeSeconds = options.ReceiveWaitTimeSeconds,
            VisibilityTimeoutSeconds = options.VisibilityTimeoutSeconds,
            CacheSize = options.CacheSize,
            DataAdapterKey = options.DataAdapterKey,
            ReceiveMessageAttributes = CopyValues(options.ReceiveMessageAttributes, nameof(options.ReceiveMessageAttributes)),
            ReceiveMessageSystemAttributes = CopyValues(options.ReceiveMessageSystemAttributes, nameof(options.ReceiveMessageSystemAttributes)),
        };

        foreach (var queueName in GetPhysicalQueueNames(providerName, result))
        {
            if (queueName.Length > 80 || !SqsQueueNamePattern().IsMatch(queueName))
            {
                throw new ArgumentException(
                    $"The generated SQS queue name '{queueName}' must be at most 80 characters and contain only letters, numbers, hyphens, underscores, and an optional .fifo suffix.",
                    nameof(options));
            }
        }

        return result;
    }

    private static IReadOnlyList<IResourceBuilder<IConstructResource<Queue>>> CreateQueues(
        IResourceBuilder<IStackResource> stack,
        string providerName,
        SqsStreamingOptions options)
    {
        var queueNames = GetPhysicalQueueNames(providerName, options);
        var result = new List<IResourceBuilder<IConstructResource<Queue>>>(queueNames.Count);
        for (var index = 0; index < queueNames.Count; index++)
        {
            var queueName = queueNames[index];
            result.Add(
                stack.AddSQSQueue(
                    $"{NormalizeResourceName(providerName)}-{index}",
                    new QueueProps
                    {
                        QueueName = queueName,
                        Fifo = options.FifoQueue,
                        ContentBasedDeduplication = options.FifoQueue,
                        DeduplicationScope = options.FifoQueue ? DeduplicationScope.MESSAGE_GROUP : null,
                        FifoThroughputLimit = options.FifoQueue ? FifoThroughputLimit.PER_MESSAGE_GROUP_ID : null,
                        ReceiveMessageWaitTime = options.ReceiveWaitTimeSeconds is { } receiveWaitTime
                            ? Duration.Seconds(receiveWaitTime)
                            : null,
                        VisibilityTimeout = options.VisibilityTimeoutSeconds is { } visibilityTimeout
                            ? Duration.Seconds(visibilityTimeout)
                            : null,
                    }));
        }

        return result.AsReadOnly();
    }

    private static IReadOnlyList<string> GetPhysicalQueueNames(
        string providerName,
        SqsStreamingOptions options)
        => Enumerable.Range(0, options.PartitionCount)
            .Select(partition => SqsQueueName.Create(providerName, partition, options.FifoQueue, options.ServiceId))
            .ToArray();

    private static IReadOnlyList<string> CopyValues(IReadOnlyList<string>? values, string name)
    {
        if (values is null)
        {
            throw new ArgumentNullException(name);
        }

        var result = new string[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(values[index]))
            {
                throw new ArgumentException($"{name} cannot contain empty values.", name);
            }

            result[index] = values[index];
        }

        return Array.AsReadOnly(result);
    }

    private static void ValidateRange(int? value, int minimum, int maximum, string name)
    {
        if (value is not null && (value < minimum || value > maximum))
        {
            throw new ArgumentOutOfRangeException(name, $"{name} must be between {minimum} and {maximum}.");
        }
    }

    private static string NormalizeResourceName(string value)
    {
        var result = new StringBuilder(value.Length);
        var previousWasHyphen = false;
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                result.Append(char.ToLowerInvariant(character));
                previousWasHyphen = false;
            }
            else if (!previousWasHyphen)
            {
                result.Append('-');
                previousWasHyphen = true;
            }
        }

        return result.ToString().Trim('-');
    }

    internal static void ValidateServiceId(
        OrleansService orleansService,
        string expectedServiceId,
        bool allowUnset)
    {
        var configuredServiceId = GetServiceId(orleansService);
        if (configuredServiceId is not string && allowUnset)
        {
            return;
        }

        if (configuredServiceId is not string serviceId)
        {
            throw new InvalidOperationException(
                "SQS streaming requires Orleans ServiceId to be a concrete string.");
        }

        if (!string.Equals(serviceId, expectedServiceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"SQS streaming ServiceId '{expectedServiceId}' conflicts with Orleans ServiceId '{serviceId}'.");
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ServiceId")]
    private static extern object? GetServiceId(OrleansService orleansService);

    [GeneratedRegex(@"^[A-Za-z0-9_-]+(?:\.fifo)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SqsQueueNamePattern();
}
