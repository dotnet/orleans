using System;
using System.Collections.Generic;

namespace Orleans.Runtime.Configuration
{
    internal static class GrainServiceConfigurationsUtility
    {
        internal static void RegisterGrainService(GrainServiceConfigurations grainServicesConfig, string serviceName, string serviceType, IDictionary<string, string> properties = null)
        {
            if (grainServicesConfig.GrainServices.ContainsKey(serviceName))
                throw new InvalidOperationException(
                    string.Format("Grain service of with name '{0}' has been already registered", serviceName));

            var config = new GrainServiceConfiguration(
                properties ?? new Dictionary<string, string>(),
                serviceName, serviceType);

            grainServicesConfig.GrainServices.Add(config.Name, config);
        }
    }
}