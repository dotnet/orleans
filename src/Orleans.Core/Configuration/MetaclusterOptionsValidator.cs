using System;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Configuration;

internal sealed class MetaclusterOptionsValidator(IOptions<MetaclusterOptions> options) : IConfigurationValidator
{
    public void ValidateConfiguration()
    {
        var value = options.Value;
        if (!value.Enabled)
        {
            return;
        }

        if (value.ClusterOwnershipLeaseDuration <= TimeSpan.Zero)
        {
            throw new OrleansConfigurationException(
                $"{nameof(MetaclusterOptions)}.{nameof(MetaclusterOptions.ClusterOwnershipLeaseDuration)} must be positive.");
        }

        if (value.ClusterOwnershipLeaseRenewalWindow <= TimeSpan.Zero
            || value.ClusterOwnershipLeaseRenewalWindow >= value.ClusterOwnershipLeaseDuration)
        {
            throw new OrleansConfigurationException(
                $"{nameof(MetaclusterOptions)}.{nameof(MetaclusterOptions.ClusterOwnershipLeaseRenewalWindow)} must be positive and shorter than the ownership lease duration.");
        }

        if (value.ClusterLocationCacheDuration < TimeSpan.Zero)
        {
            throw new OrleansConfigurationException(
                $"{nameof(MetaclusterOptions)}.{nameof(MetaclusterOptions.ClusterLocationCacheDuration)} cannot be negative.");
        }

        foreach (var cluster in value.Clusters)
        {
            if (string.IsNullOrWhiteSpace(cluster.Key))
            {
                throw new OrleansConfigurationException("Metacluster cluster identities must be non-empty.");
            }

            if (cluster.Value is null)
            {
                throw new OrleansConfigurationException(
                    $"Metacluster relay endpoints for cluster '{cluster.Key}' must be initialized.");
            }

            foreach (var endpoint in cluster.Value)
            {
                if (endpoint is null || !endpoint.IsAbsoluteUri)
                {
                    throw new OrleansConfigurationException(
                        $"Metacluster relay endpoint for cluster '{cluster.Key}' must be an absolute URI.");
                }
            }
        }
    }
}
