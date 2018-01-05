using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Host;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace OrleansSilo
{
    public class OrleansHostWrapper
    {
        private readonly SiloHost _siloHost;
        public OrleansHostWrapper(ClusterConfiguration clusterConfiguration)
        {
            _siloHost = new SiloHost(Dns.GetHostName(), clusterConfiguration);
            _siloHost.LoadOrleansConfig();
        }

        public int Run()
        {
            if(_siloHost == null)
            {
                return 1;
            }
            try
            {
                _siloHost.InitializeOrleansSilo();
                if(_siloHost.StartOrleansSilo())
                {
                    Console.WriteLine($"Successfully started Silo '{_siloHost.Name}' as a {_siloHost.Type} node.");
                    return 0;
                }
                else
                {
                    throw new OrleansException($"Fail to start Orleans silo '{_siloHost.Name}' as {_siloHost.Type}");
                }
            }
            catch(Exception ex)
            {
                _siloHost.ReportStartupError(ex);
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        public int Stop()
        {
            if(_siloHost != null)
            {
                try
                {
                    _siloHost.StopOrleansSilo();
                    _siloHost.Dispose();
                    Console.WriteLine($"Orleans silo '{_siloHost.Name}' shutdown.");
                }
                catch(Exception ex)
                {
                    _siloHost.ReportStartupError(ex);
                    Console.Error.WriteLine(ex);
                    return 1;
                }
            }
            return 0;
        }
    }
}
