using System;
using System.Net;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Host;

namespace OrleansSilo
{
    public class OrleansHostWrapper
    {
        private readonly SiloHost siloHost;

        public OrleansHostWrapper(ClusterConfiguration config)
        {
            siloHost = new SiloHost(Dns.GetHostName(), config);
            siloHost.LoadOrleansConfig();
        }

        public int Run()
        {
            if (siloHost == null)
            {
                return 1;
            }

            try
            {
                siloHost.InitializeOrleansSilo();

                if (siloHost.StartOrleansSilo())
                {
                    Console.WriteLine($"Successfully started Orleans silo '{siloHost.Name}' as a {siloHost.Type} node.");
                    return 0;
                }
                else
                {
                    throw new OrleansException($"Failed to start Orleans silo '{siloHost.Name}' as a {siloHost.Type} node.");
                }
            }
            catch (Exception exc)
            {
                siloHost.ReportStartupError(exc);
                Console.Error.WriteLine(exc);
                return 1;
            }
        }

        public int Stop()
        {
            if (siloHost != null)
            {
                try
                {
                    siloHost.StopOrleansSilo();
                    siloHost.Dispose();
                    Console.WriteLine($"Orleans silo '{siloHost.Name}' shutdown.");
                }
                catch (Exception exc)
                {
                    siloHost.ReportStartupError(exc);
                    Console.Error.WriteLine(exc);
                    return 1;
                }
            }
            return 0;
        }
    }
}