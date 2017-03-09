using System.Collections.Generic;

namespace Orleans.Runtime.Configuration
{
    public interface IGrainServiceConfiguration
    {
        string Name { get; set; }
        string ServiceType { get; set; }
        IDictionary<string, string> Properties { get; set; }
    }
}