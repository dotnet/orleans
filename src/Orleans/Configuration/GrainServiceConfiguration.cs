using System;
using System.Collections.Generic;
using System.Xml;

namespace Orleans.Runtime.Configuration
{
    [Serializable]
    public class GrainServiceConfiguration : IGrainServiceConfiguration
    {

        public string Name { get; set; }
        public string ServiceType { get; set; }
        public IDictionary<string, string> Properties { get; set; }

        public GrainServiceConfiguration() {}

        public GrainServiceConfiguration(IDictionary<string, string> properties, string serviceName, string serviceType)
        {
            Properties = properties;
            Name = serviceName;
            ServiceType = serviceType;
        }

        internal void Load(XmlElement child, IDictionary<string, IGrainServiceConfiguration> alreadyLoaded, XmlNamespaceManager nsManager)
        {
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
                return;
            }

            if (child.HasAttribute("ServiceType"))
            {
                ServiceType = child.GetAttribute("ServiceType");
            }
            else
            {
                throw new FormatException("Missing 'ServiceType' attribute on 'GrainService' element");
            }

            if (String.IsNullOrEmpty(Name))
            {
                Name = ServiceType;
            }

            Properties = new Dictionary<string, string>();
            for (int i = 0; i < child.Attributes.Count; i++)
            {
                var key = child.Attributes[i].LocalName;
                var val = child.Attributes[i].Value;
                if ((key != "Type") && (key != "Name"))
                {
                    Properties[key] = val;
                }
            }
        }
        
        internal static void LoadProviderConfigurations(XmlElement root, XmlNamespaceManager nsManager,
            IDictionary<string, IGrainServiceConfiguration> alreadyLoaded, Action<IGrainServiceConfiguration> add)
        {
            var nodes = root.SelectNodes("orleans:GrainService", nsManager);
            foreach (var node in nodes)
            {
                var subElement = node as XmlElement;
                if (subElement == null) continue;

                var config = new GrainServiceConfiguration();
                config.Load(subElement, alreadyLoaded, nsManager);
                add(config);
            }
        }
    }
}