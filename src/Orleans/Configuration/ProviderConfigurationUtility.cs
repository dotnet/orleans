using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Orleans.Providers;

namespace Orleans.Runtime.Configuration
{
    internal class ProviderConfigurationUtility
    {
        internal static void RegisterProvider(IDictionary<string, ProviderCategoryConfiguration> providerConfigurations, string providerCategory, string providerTypeFullName, string providerName, IDictionary<string, string> properties = null)
        {
            if (string.IsNullOrEmpty(providerCategory))
                throw new ArgumentException("Provider Category cannot be null or empty string", "providerCategory");

            if (string.IsNullOrEmpty(providerTypeFullName))
                throw new ArgumentException("Provider type full name cannot be null or empty string", "providerTypeFullName");

            if (string.IsNullOrEmpty(providerName))
                throw new ArgumentException("Provider name cannot be null or empty string", "providerName");

            ProviderCategoryConfiguration category;
            if (!providerConfigurations.TryGetValue(providerCategory, out category))
            {
                category = new ProviderCategoryConfiguration(providerCategory);
                providerConfigurations.Add(category.Name, category);
            }

            if (category.Providers.ContainsKey(providerName))
                throw new InvalidOperationException(
                    string.Format("{0} provider of type {1} with name '{2}' has been already registered", providerCategory, providerTypeFullName, providerName));

            var config = new ProviderConfiguration(
                properties ?? new Dictionary<string, string>(),
                providerTypeFullName, providerName);

            category.Providers.Add(config.Name, config);
        }

        internal static bool TryGetProviderConfiguration(IDictionary<string, ProviderCategoryConfiguration> providerConfigurations, 
            string providerTypeFullName, string providerName, out IProviderConfiguration config)
        {
            foreach (ProviderCategoryConfiguration category in providerConfigurations.Values)
            {
                foreach (IProviderConfiguration providerConfig in category.Providers.Values)
                {
                    if (providerConfig.Type.Equals(providerTypeFullName) && providerConfig.Name.Equals(providerName))
                    {
                        config = providerConfig;
                        return true;
                    }
                }
            }
            config = null;
            return false;
        }

        internal static IEnumerable<IProviderConfiguration> GetAllProviderConfigurations(IDictionary<string, ProviderCategoryConfiguration> providerConfigurations)
        {
            return providerConfigurations.Values.SelectMany(category => category.Providers.Values);
        }

        internal static string PrintProviderConfigurations(IDictionary<string, ProviderCategoryConfiguration> providerConfigurations)
        {
            var sb = new StringBuilder();
            if (providerConfigurations.Keys.Count > 0)
            {
                foreach (string provType in providerConfigurations.Keys)
                {
                    ProviderCategoryConfiguration provTypeConfigs = providerConfigurations[provType];
                    sb.AppendFormat("       {0}Providers:", provType)
                        .AppendLine();
                    sb.AppendFormat(provTypeConfigs.ToString())
                        .AppendLine();
                }
            }
            else
            {
                sb.AppendLine("       No providers configured.");
            }
            return sb.ToString();
        }
    }
}