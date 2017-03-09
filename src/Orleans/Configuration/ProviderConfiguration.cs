using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
}
