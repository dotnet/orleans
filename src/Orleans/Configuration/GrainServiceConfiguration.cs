using System;
using System.Collections.Generic;
using System.Xml;

namespace Orleans.Runtime.Configuration
{
    [Serializable]
    public class GrainServiceConfigurations
    {
        public IDictionary<string, IGrainServiceConfiguration> GrainServices { get; set; }

        public GrainServiceConfigurations()
        {
            GrainServices = new Dictionary<string, IGrainServiceConfiguration>();
        }

        internal static GrainServiceConfigurations Load(XmlElement child)
        {
            var container = new GrainServiceConfigurations();

            var nsManager = new XmlNamespaceManager(new NameTable());
            nsManager.AddNamespace("orleans", "urn:orleans");

            GrainServiceConfiguration.LoadProviderConfigurations(
                child, nsManager, container.GrainServices,
                c => container.GrainServices.Add(c.Name, c));

            return container;
        }
    }

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

    public interface IGrainServiceConfiguration
    {
        string Name { get; set; }
        string ServiceType { get; set; }
        IDictionary<string, string> Properties { get; set; }
    }

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