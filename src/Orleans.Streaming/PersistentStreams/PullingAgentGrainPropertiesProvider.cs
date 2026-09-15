using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans.Streams;

internal sealed record PullingAgentProviderRegistration(string Name);

internal sealed class PullingAgentGrainPropertiesProvider(
    IEnumerable<PullingAgentProviderRegistration> providers,
    IOptionsMonitor<StreamPullingAgentOptions> options) : IGrainPropertiesProvider
{
    public void Populate(Type grainClass, GrainType grainType, Dictionary<string, string> properties)
    {
        if (grainClass != typeof(PullingAgentGrain) && grainClass != typeof(PullingAgentCoordinatorGrain))
        {
            return;
        }

        foreach (var provider in providers)
        {
            if (options.Get(provider.Name).HostingMode == StreamPullingAgentHostingMode.Grain)
            {
                properties[PullingAgentPlacementDirector.ProviderPropertyPrefix + provider.Name] = "true";
            }
        }
    }
}
