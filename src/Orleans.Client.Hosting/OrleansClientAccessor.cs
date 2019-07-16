using Microsoft.Extensions.Hosting;
using System.Collections.Generic;
using System.Linq;
namespace Orleans.Client.Hosting
{
    public class OrleansClientAccessor : IOrleansClientAccessor
    {
        OrleansClientStore clientStore;
        public OrleansClientAccessor(OrleansClientStore clientStore)
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
    }
}
