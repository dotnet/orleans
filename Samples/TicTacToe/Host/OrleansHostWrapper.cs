/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Net;
using System.Threading.Tasks;

using Orleans.Runtime.Host;

namespace Host
{
    internal class OrleansHostWrapper : IDisposable
    {
        public bool Debug
        {
            get { return siloHost != null && siloHost.Debug; }
            set { siloHost.Debug = value; }
        }

        private SiloHost siloHost;

        public OrleansHostWrapper(string[] args)
        {
            ParseArguments(args);
            Init();
        }

        public bool Run()
        {
            bool ok = false;

            try
            {
                siloHost.InitializeOrleansSilo();

                ok = siloHost.StartOrleansSilo();

                if (ok)
                {
                    Console.WriteLine(string.Format("Successfully started Orleans silo '{0}' as a {1} node.", siloHost.Name, siloHost.Type));
                }
                else
                {
                    throw new SystemException(string.Format("Failed to start Orleans silo '{0}' as a {1} node.", siloHost.Name, siloHost.Type));
                }
            }
            catch (Exception exc)
            {
                siloHost.ReportStartupError(exc);
                var msg = string.Format("{0}:\n{1}\n{2}", exc.GetType().FullName, exc.Message, exc.StackTrace);
                Console.WriteLine(msg);
            }

            return ok;
        }

        public bool Stop()
        {
            bool ok = false;

            try
            {
                siloHost.StopOrleansSilo();

                Console.WriteLine(string.Format("Orleans silo '{0}' shutdown.", siloHost.Name));
            }
            catch (Exception exc)
            {
                siloHost.ReportStartupError(exc);
                var msg = string.Format("{0}:\n{1}\n{2}", exc.GetType().FullName, exc.Message, exc.StackTrace);
                Console.WriteLine(msg);
            }

            return ok;
        }

        private void Init()
        {
            siloHost.LoadOrleansConfig();
        }

        private bool ParseArguments(string[] args)
        {
            string deploymentId = null;

            string configFileName = "DevTestServerConfiguration.xml";
            string siloName = Dns.GetHostName(); // Default to machine name

            int argPos = 1;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a.StartsWith("-") || a.StartsWith("/"))
                {
                    switch (a.ToLowerInvariant())
                    {
                        case "/?":
                        case "/help":
                        case "-?":
                        case "-help":
                            // Query usage help
                            return false;
                        default:
                            Console.WriteLine("Bad command line arguments supplied: " + a);
                            return false;
                    }
                }
                else if (a.Contains("="))
                {
                    string[] split = a.Split('=');
                    if (String.IsNullOrEmpty(split[1]))
                    {
                        Console.WriteLine("Bad command line arguments supplied: " + a);
                        return false;
                    }
                    switch (split[0].ToLowerInvariant())
                    {
                        case "deploymentid":
                            deploymentId = split[1];
                            break;
                        case "deploymentgroup":
                            // TODO: Remove this at some point in future
                            Console.WriteLine("Ignoring deprecated command line argument: " + a);
                            break;
                        default:
                            Console.WriteLine("Bad command line arguments supplied: " + a);
                            return false;
                    }
                }
                // unqualified arguments below
                else if (argPos == 1)
                {
                    siloName = a;
                    argPos++;
                }
                else if (argPos == 2)
                {
                    configFileName = a;
                    argPos++;
                }
                else
                {
                    // Too many command line arguments
                    Console.WriteLine("Too many command line arguments supplied: " + a);
                    return false;
                }
            }

            siloHost = new SiloHost(siloName);
            siloHost.ConfigFileName = configFileName;
            if (deploymentId != null)
                siloHost.DeploymentId = deploymentId;

            return true;
        }

        public void PrintUsage()
        {
            Console.WriteLine(
@"USAGE: 
    orleans host [<siloName> [<configFile>]] [DeploymentId=<idString>] [/debug]
Where:
    <siloName>      - Name of this silo in the Config file list (optional)
    <configFile>    - Path to the Config file to use (optional)
    DeploymentId=<idString> 
                    - Which deployment group this host instance should run in (optional)");
        }

        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool dispose)
        {
            siloHost.Dispose();
            siloHost = null;
        }
    }
}
