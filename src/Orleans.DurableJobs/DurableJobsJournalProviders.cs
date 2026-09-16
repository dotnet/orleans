using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.DurableJobs;

internal sealed record DurableJobsJournalProvider(
    string Name,
    IJournalStorageProvider Storage,
    IJournalStorageCatalog Catalog,
    IJournaledStateManagerFactory Factory);

internal sealed class DurableJobsJournalProviders
{
    public DurableJobsJournalProviders(IServiceProvider services, IOptions<DurableJobsOptions> options)
    {
        var value = options.Value;
        var names = GetProviderNames(value);
        Providers = names.Select(name => Resolve(services, name)).ToArray();
    }

    internal DurableJobsJournalProviders(params DurableJobsJournalProvider[] providers)
    {
        ArgumentOutOfRangeException.ThrowIfZero(providers.Length);
        Providers = providers.ToArray();
    }

    internal DurableJobsJournalProvider[] Providers { get; }

    internal DurableJobsJournalProvider WriteProvider => Providers[0];

    internal DurableJobsJournalProvider GetProvider(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Array.Find(Providers, provider => string.Equals(provider.Name, name, StringComparison.Ordinal))
            ?? throw new ArgumentException($"Journal provider '{name}' is not selected for Durable Jobs.", nameof(name));
    }

    internal static List<string> GetProviderNames(DurableJobsOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.WriteProviderName))
        {
            throw new OrleansConfigurationException("DurableJobsOptions.WriteProviderName must be non-empty.");
        }

        var result = new List<string> { options.WriteProviderName };
        var names = new HashSet<string>(StringComparer.Ordinal) { options.WriteProviderName };
        foreach (var name in options.DrainingProviderNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new OrleansConfigurationException("DurableJobsOptions.DrainingProviderNames must contain non-empty provider names.");
            }

            if (!names.Add(name))
            {
                throw new OrleansConfigurationException($"Durable Jobs journal provider '{name}' is selected more than once.");
            }

            result.Add(name);
        }

        return result;
    }

    private static DurableJobsJournalProvider Resolve(IServiceProvider services, string name)
    {
        try
        {
            return new(
                name,
                services.GetRequiredKeyedService<IJournalStorageProvider>(name),
                services.GetRequiredKeyedService<IJournalStorageCatalog>(name),
                services.GetRequiredKeyedService<IJournaledStateManagerFactory>(name));
        }
        catch (Exception exception)
        {
            throw new OrleansConfigurationException(
                $"Durable Jobs journal provider '{name}' requires registered storage, catalog, and state-manager factory services.",
                exception);
        }
    }
}
