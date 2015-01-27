/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

﻿using System;
using System.Collections.Generic;
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
        private IList<ProviderConfiguration> childConfigurations;
        private IList<IProvider> childProviders;
        [NonSerialized]
        private IProviderManager providerManager;

        internal string Type { get; private set; }
        public string Name { get; private set; }

        /// <summary>
        /// Properties of this provider.
        /// </summary>
        public IDictionary<string, string> Properties { get { return new Dictionary<string, string>(properties); } }

        internal ProviderConfiguration()
        {
            properties = new Dictionary<string, string>();
        }

        public ProviderConfiguration(IDictionary<string, string> properties, string type, string name)
        {
            this.properties = properties;
            Type = type;
            Name = name;
        }

        // for testing purposes
        internal ProviderConfiguration(IDictionary<string, string> properties, IList<IProvider> childProviders)
        {
            this.properties = properties;
            this.childProviders = childProviders;
        }

        // Load from an element with the format <Provider Type="..." Name="...">...</Provider>
        internal void Load(XmlElement child, IDictionary<string, IProviderConfiguration> alreadyLoaded, XmlNamespaceManager nsManager)
        {
            childConfigurations = new List<ProviderConfiguration>();

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

        internal void AddProperty(string key, string val)
        {
            properties.Add(key, val);
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
        public string Name { get; set; }

        public IDictionary<string, IProviderConfiguration> Providers { get; set; }

        // Load from an element with the format <NameProviders>...</NameProviders>
        // that contains a sequence of Provider elements (see the ProviderConfiguration type)
        internal void Load(XmlElement child)
        {
            if (!child.LocalName.EndsWith("Providers", StringComparison.Ordinal))
                throw new FormatException("Providers node name is not correct at element " + child.LocalName);

            Name = child.LocalName.Substring(0, child.LocalName.Length - 9);

            Providers = new Dictionary<string, IProviderConfiguration>();
            var nsManager = new XmlNamespaceManager(new NameTable());
            nsManager.AddNamespace("orleans", "urn:orleans");
            ProviderConfiguration.LoadProviderConfigurations(child, nsManager, Providers, c => Providers.Add(c.Name, c));
        }

        internal void AddToConfiguration(string key, string val)
        {
            foreach (IProviderConfiguration config in Providers.Values)
            {
                ((ProviderConfiguration)config).AddProperty(key, val);
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
