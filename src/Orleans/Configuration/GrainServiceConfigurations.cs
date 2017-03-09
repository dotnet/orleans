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
}