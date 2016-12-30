using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Xml;
using Orleans.Providers;

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Configuration for a particular provider instance.
    /// </summary>
    [Serializable]
    public class ProviderConfiguration : IProviderConfiguration
    {
        private IDictionary<string, string> properties;
        private readonly IList<ProviderConfiguration> childConfigurations = new List<ProviderConfiguration>();
        [NonSerialized]
        private IList<IProvider> childProviders;
        [NonSerialized]
        private IProviderManager providerManager;

        public string Type { get; private set; }
        public string Name { get; private set; }
        public IProviderManager ProviderManager {get { return providerManager; } }

        public void AddChildConfiguration(IProviderConfiguration config)
        {
            childConfigurations.Add(config as ProviderConfiguration);
        }

        private ReadOnlyDictionary<string, string> readonlyCopyOfProperties;
        /// <summary>
        /// Properties of this provider.
        /// </summary>
        public ReadOnlyDictionary<string, string> Properties 
        {
            get
            {
                if (readonlyCopyOfProperties == null)
                {
                    readonlyCopyOfProperties = new ReadOnlyDictionary<string, string>(properties);
                }
                return readonlyCopyOfProperties;
            } 
        }

        internal ProviderConfiguration()
        {
            properties = new Dictionary<string, string>();
        }

        public ProviderConfiguration(IDictionary<string, string> properties, string providerType, string name)
        {
            this.properties = properties ?? new Dictionary<string, string>(1);
            Type = providerType;
            Name = name;
        }

        // for testing purposes
        internal ProviderConfiguration(IDictionary<string, string> properties, IList<IProvider> childProviders)
        {
            this.properties = properties ?? new Dictionary<string, string>(1);
            this.childProviders = childProviders;
        }

        // Load from an element with the format <Provider Type="..." Name="...">...</Provider>
        internal void Load(XmlElement child, IDictionary<string, IProviderConfiguration> alreadyLoaded, XmlNamespaceManager nsManager)
        {
            readonlyCopyOfProperties = null; // cause later refresh of readonlyCopyOfProperties
            if (nsManager == null)
            {
                nsManager = new XmlNamespaceManager(new NameTable());
                nsManager.AddNamespace("orleans", "urn:orleans");
            }

            if (child.HasAttribute("Name"))
            {
                Name = child.GetAttribute("Name");
            }

            if (alreadyLoaded != null && alreadyLoaded.ContainsKey(Name))
            {
                // This is just a reference to an already defined provider

                var provider = (ProviderConfiguration)alreadyLoaded[Name];
                properties = provider.properties;
                Type = provider.Type;
                return;
            }

            if (child.HasAttribute("Type"))
            {
                Type = child.GetAttribute("Type");
            }
            else
            {
                throw new FormatException("Missing 'Type' attribute on 'Provider' element");
            }

            if (String.IsNullOrEmpty(Name))
            {
                Name = Type;
            }

            properties = new Dictionary<string, string>();
            for (int i = 0; i < child.Attributes.Count; i++)
            {
                var key = child.Attributes[i].LocalName;
                var val = child.Attributes[i].Value;
                if ((key != "Type") && (key != "Name"))
                {
                    properties[key] = val;
                }
            }

            LoadProviderConfigurations(child, nsManager, alreadyLoaded, c => childConfigurations.Add(c));
        }

        internal static void LoadProviderConfigurations(XmlElement root, XmlNamespaceManager nsManager,
            IDictionary<string, IProviderConfiguration> alreadyLoaded, Action<ProviderConfiguration> add)
        {
            var nodes = root.SelectNodes("orleans:Provider", nsManager);
            foreach (var node in nodes)
            {
                var subElement = node as XmlElement;
                if (subElement == null) continue;

                var config = new ProviderConfiguration();
                config.Load(subElement, alreadyLoaded, nsManager);
                add(config);
            }
        }

        internal void SetProviderManager(IProviderManager manager)
        {
            this.providerManager = manager;
            foreach (var child in childConfigurations)
                child.SetProviderManager(manager);
        }

        public void SetProperty(string key, string val)
        {
            readonlyCopyOfProperties = null; // cause later refresh of readonlyCopyOfProperties
            if (!properties.ContainsKey(key))
            {
                properties.Add(key, val);
            }
            else
            {
                // reset the property.
                properties.Remove(key);
                properties.Add(key, val);
            }
        }

        public bool RemoveProperty(string key)
        {
            readonlyCopyOfProperties = null; // cause later refresh of readonlyCopyOfProperties
            return properties.Remove(key);
        }

        public override string ToString()
        {
            // print only _properties keys, not values, to not leak application sensitive data.
            var propsStr = properties == null ? "Null" : Utils.EnumerableToString(properties.Keys);
            return string.Format("Name={0}, Type={1}, Properties={2}", Name, Type, propsStr);
        }

        /// <summary>
        /// Children providers of this provider. Used by hierarchical providers.
        /// </summary>
        public IList<IProvider> Children
        {
            get
            {
                if (childProviders != null)
                    return new List<IProvider>(childProviders); //clone

                var list = new List<IProvider>();

                if (childConfigurations.Count == 0)
                    return list; // empty list

                foreach (var config in childConfigurations)
                    list.Add(providerManager.GetProvider(config.Name));

                childProviders = list;
                return new List<IProvider>(childProviders); // clone
            }
        }
    }

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
