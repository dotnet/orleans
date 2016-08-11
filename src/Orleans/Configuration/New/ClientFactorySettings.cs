using System.Collections.Generic;
using Orleans.Configuration.New;

namespace Orleans.Runtime.Configuration.New
{
    public class ClientFactorySettings
    {

        public Messaging Messaging { get; set; } = new Messaging();
        public Tracing Tracing { get; set; } = new Tracing();
        public Statistics Statistics { get; set; } = new Statistics();
        public List<Gateway> Gateways { get; set; } = new List<Gateway>();
        public SystemStore SystemStore { get; set; } = new SystemStore();
        public List<LimitValue> Limits { get; set; } = new List<LimitValue>();
        public LocalAddress LocalAddress { get; set; } = new LocalAddress();
        
    }
}