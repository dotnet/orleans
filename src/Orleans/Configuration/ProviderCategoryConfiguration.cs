using System;
using System.Collections.Generic;
using System.Text;
using System.Xml;
using Orleans.Providers;

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Provider categoty configuration.
    /// </summary>
    [Serializable]
    public class ProviderCategoryConfiguration
    {
        public const string BOOTSTRAP_PROVIDER_CATEGORY_NAME = "Bootstrap";
        public const string STORAGE_PROVIDER_CATEGORY_NAME = "Storage";
        public const string STREAM_PROVIDER_CATEGORY_NAME = "Stream";
        public const string LOG_CONSISTENCY_PROVIDER_CATEGORY_NAME = "LogConsistency";
        public const string STATISTICS_PROVIDER_CATEGORY_NAME = "Statistics";

        public string Name { get; set; }
        public IDictionary<string, IProviderConfiguration> Providers { get; set; }

        public ProviderCategoryConfiguration(string name)
        {
            Name = name;
            Providers = new Dictionary<string, IProviderConfiguration>();
        }

        // Load from an element with the format <NameProviders>...</NameProviders>
        // that contains a sequence of Provider elements (see the ProviderConfiguration type)
        internal static ProviderCategoryConfiguration Load(XmlElement child)
        {
            string name = child.LocalName.Substring(0, child.LocalName.Length - 9);

            var category = new ProviderCategoryConfiguration(name);

            var nsManager = new XmlNamespaceManager(new NameTable());
            nsManager.AddNamespace("orleans", "urn:orleans");

            ProviderConfiguration.LoadProviderConfigurations(
                child, nsManager, category.Providers,
                c => category.Providers.Add(c.Name, c));
            return category;
        }

        internal void Merge(ProviderCategoryConfiguration other)
        {
            foreach (var provider in other.Providers)
            {
                Providers.Add(provider);
            }
        }

        internal void SetConfiguration(string key, string val)
        {
            foreach (IProviderConfiguration config in Providers.Values)
            {
                ((ProviderConfiguration)config).SetProperty(key, val);
            }
        }

        internal static string ProviderConfigsToXmlString(IDictionary<string, ProviderCategoryConfiguration> providerConfig)
        {
            var sb = new StringBuilder();
            foreach (string category in providerConfig.Keys)
            {
                var providerInfo = providerConfig[category];
                string catName = providerInfo.Name;
                sb.AppendFormat("<{0}Providers>", catName).AppendLine();
                foreach (string provName in providerInfo.Providers.Keys)
                {
                    var prov = (ProviderConfiguration) providerInfo.Providers[provName];
                    sb.AppendLine("<Provider");
                    sb.AppendFormat("  Name=\"{0}\"", provName).AppendLine();
                    sb.AppendFormat("  Type=\"{0}\"", prov.Type).AppendLine();
                    foreach (var propName in prov.Properties.Keys)
                    {
                        sb.AppendFormat("  {0}=\"{1}\"", propName, prov.Properties[propName]).AppendLine();
                    }
                    sb.AppendLine(" />");
                }
                sb.AppendFormat("</{0}Providers>", catName).AppendLine();
            }
            return sb.ToString();
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            foreach (var kv in Providers)
            {
                sb.AppendFormat("           {0}", kv.Value).AppendLine();
            }
            return sb.ToString();
        }
    }
}