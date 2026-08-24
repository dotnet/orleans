#if NET10_0_OR_GREATER

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace TestExtensions;

internal sealed class AspireResourceConfiguration : ConfigurationProvider, IConfigurationSource
{
    private AspireResourceConfiguration(Dictionary<string, string?> values)
    {
        Data = values;
    }

    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

    public static async Task<IConfigurationRoot> CreateAsync(
        IResource resource,
        IServiceProvider services,
        DistributedApplicationOperation operation = DistributedApplicationOperation.Run,
        Func<string, bool>? include = null)
    {
        var executionContext = new DistributedApplicationExecutionContext(
            new DistributedApplicationExecutionContextOptions(operation)
            {
                ServiceProvider = services,
            });
        var environment = new Dictionary<string, object>();
        var callbackContext = new EnvironmentCallbackContext(executionContext, resource, environment);

        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await annotation.Callback(callbackContext);
        }

        var valueContext = new ValueProviderContext
        {
            Caller = resource,
            ExecutionContext = executionContext,
            Network = KnownNetworkIdentifiers.LocalhostNetwork,
        };
        var configuration = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in environment)
        {
            if (include is not null && !include(key))
            {
                continue;
            }

            configuration[key.Replace("__", ":", StringComparison.Ordinal)] = value switch
            {
                null => null,
                IValueProvider provider => await provider.GetValueAsync(valueContext),
                _ => value.ToString(),
            };
        }

        return new ConfigurationBuilder()
            .Add(new AspireResourceConfiguration(configuration))
            .Build();
    }
}

#endif
