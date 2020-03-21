using System;
using Orleans.Runtime.Configuration;

namespace OrleansSiloHost
{
    /// <summary>
    /// Orleans test host
    /// </summary>
    public class Program
    {
        private static OrleansHostWrapper hostWrapper;
        static int Main(string[] args)
        {
            int exitCode = StartSilo(args);

            Console.WriteLine("Press Enter to terminate...");
            Console.ReadLine();

            exitCode += ShutdownSilo();

            //either StartSilo or ShutdownSilo failed would result on a non-zero exit code. 
            return exitCode;
        }


        private static int StartSilo(string[] args)
        {
            // define the cluster configuration
            var config = ClusterConfiguration.LocalhostPrimarySilo();
            config.AddMemoryStorageProvider();

            hostWrapper = new OrleansHostWrapper(config, args);
            return hostWrapper.Run();
        }

        private static int ShutdownSilo()
        {
            if (hostWrapper != null)
            {
                return hostWrapper.Stop();
            }
            return 0;
        }
    }
}
