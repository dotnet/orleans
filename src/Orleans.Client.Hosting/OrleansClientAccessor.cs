using Microsoft.Extensions.Hosting;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.Client.Hosting
{
    public class OrleansClientAccessor : IOrleansClientAccessor
    {
        OrleansClientHostedService hostedService;
        public OrleansClientAccessor(IEnumerable<IHostedService> hostedServices)
        {
            this.hostedService = hostedServices.OfType<OrleansClientHostedService>().Last();
        }
        public IClusterClient Client
        {
            get
            {
                return this.hostedService.Client;
            }
        }
    }
}
