using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Client.Hosting
{
    public class OrleansHostedClientStore
    {
        private const string Default_Client_Name= "Default";
        private Dictionary<string, IClusterClient> clients = new Dictionary<string, IClusterClient>();
        public IClusterClient Client
        { get
            {
                return this.GetClient(Default_Client_Name);
            }
            set
            {
                this.SetClient(Default_Client_Name, value);
            }
        }

        public IClusterClient GetClient(string name)
        {
            if (clients.ContainsKey(name))
                return clients[name];
            else
                return null;
        }

        public void SetClient(string name, IClusterClient client)
        {
            if (string.IsNullOrWhiteSpace(name))
                name = Default_Client_Name;

            if (clients.ContainsKey(name))
                clients[name] = client;
            else
                clients.Add(name, client);
        }
    }
}
