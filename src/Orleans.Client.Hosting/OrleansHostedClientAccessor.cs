using Microsoft.Extensions.Hosting;
using System.Collections.Generic;
using System.Linq;
namespace Orleans.Client.Hosting
{
    public class OrleansHostedClientAccessor : IOrleansHostedClientAccessor
    {
        OrleansHostedClientStore clientStore;
        public OrleansHostedClientAccessor(OrleansHostedClientStore clientStore)
        {
            this.clientStore = clientStore;
        }
        public IClusterClient Client
        {
            get
            {
                return this.clientStore.Client;
            }
        }

        public IClusterClient GetClient(string name)
        {
                return this.clientStore.GetClient(name);

        }
    }
}
