using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using Orleans;
using LoadTestBase;

namespace PresenceConsoleTest
{
    class PresenceWorkerDriver
    {
        private static bool warmup = false;
        private static bool heartbeatFormatTU1 = true;
        private static int num_users = 500 * 1000;
        private static int num_stages = 1;

        private static string CommandLineHelp()
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("USAGE:");
            sb.AppendLine("    PresenceConsoleTest [-w | -n (# of requests) | -users (# of users)");
            sb.AppendLine("                     | -pipe (pipeline size) | -threads (# of worker threads)"); 
            sb.AppendLine("                     | -port (gateway default port)");
            sb.AppendLine("                     | -instanceIndex (index of the instance)] (GatewayEndpoint)");
            sb.AppendLine("where");
            sb.AppendLine("    GatewayEndpoint                  List of IPs to use for gateways.  This list ");
            sb.AppendLine("                                     is space separated.  i.e. IP#1 IP#2 ...");
            sb.AppendLine("    Options:");
            sb.AppendLine("       -w                            Precreates the user and session grains.");
            sb.AppendLine("       -n                            Number of requests.  Defaults to 100000000.");
            sb.AppendLine("       -users or -u                  Number of users.  Defaults to 500000.");
            sb.AppendLine("       -stages                       Number of stages.  Defaults to 1.");
            sb.AppendLine("       -pipe or -pipeline or -p      Pipeline size.  Defaults to 500.");
            sb.AppendLine("       -threads or -t                Number of work threads.  Defaults to 5.");
            sb.AppendLine("       -port                         Gateway's port number. Defaults to 30000.");
            sb.AppendLine("       -instanceIndex                The index number of the Gateway to use ");
            sb.AppendLine("                                     from ClientConfiguration.xml.");
            sb.AppendLine("       -tu1                          Send heartbeats inTU1 format");
            sb.AppendLine("       -azure                        Use gateway list from Azure table OrleansSiloInstances rather than config file");
            sb.AppendLine();
            sb.AppendLine("Examples:");
            sb.AppendLine("     > PresenceConsoleTest                       ... Show information");
            sb.AppendLine("     > PresenceConsoleTest MachineName           ... Runs with all defaults");
            sb.AppendLine("                                                     with one gateway.");
            sb.AppendLine("     > PresenceConsoleTest -w MachineName        ... Runs with warm-up.");
            sb.AppendLine("     > PresenceConsoleTest -w -n 1000 -u 50 -t 2 MachineName");
            return sb.ToString();
        }

        private static bool ParseApplicationArguments(string[] args)
        {
            if (args.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].Trim();

                if (a == "-?" || a == "-help" || a == "/?" || a == "/help")
                {
                    return false;
                }
                else if (a == "-w") // precreates the users and sessions
                {
                    warmup = true;
                }
                else if (a == "-tu1")
                {
                    heartbeatFormatTU1 = true;
                }
                else if (a == "-users" || a == "-u") // number of users
                {
                    num_users = int.Parse(args[++i]);
                }
                else if (a == "-stages")
                {
                    num_stages = int.Parse(args[++i]);
                }
            }

            return true;
        }

        public static void Main(string[] args)
        {
            LoadTestDriverBase driver = new LoadTestDriverBase("PresenceWorker");

            var config = driver.InitConfig(args, null);
            bool ok = driver.Initialze(typeof(PresenceWorker));
            if(!ok)
            {
                return;
            }
            LoadTestDriverBase.WriteProgress("Done Initialze by all workers");

            ok = ParseApplicationArguments(args);
            if (!ok)
            {
                LoadTestDriverBase.WriteProgress(CommandLineHelp());
                return;
            }

            LoadTestDriverBase.WriteProgress("Additional parameterization: {0}", PrintParams());

            for (int i = 0; i < driver.Workers.Length; i++)
            {
                ((PresenceWorker)driver.Workers[i]).ApplicationInitialize(
                    num_users,
                    num_stages,
                    heartbeatFormatTU1,
                    warmup && i == 0);

                Thread.Sleep(10);
            }
            
            LoadTestDriverBase.WriteProgress("Done ApplicationInitialize by all workers");

            driver.Run();

            //driver.Uninitialize();

            driver.WriteTestResults();
            
            LoadTestDriverBase.WriteProgress("Done the whole test");
        }
        private static string PrintParams()
        {
            return String.Format("num_users = {0}, num_stages = {1}", num_users, num_stages);
        }

    }
}
