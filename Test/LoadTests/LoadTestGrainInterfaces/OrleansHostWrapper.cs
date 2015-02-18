using System;
using System.Net;

using Orleans.Runtime.Host;


namespace LoadTestGrainInterfaces
{
        public class OrleansHostWrapper : IDisposable
        {
            public string SiloConfigFile { get; private set; }
            public string SiloName { get; private set; }
            public string DeploymentId { get; private set; }
            public bool Verbose { get; private set; }
            private SiloHost _siloHost;

            public OrleansHostWrapper(string siloConfigFile, string siloName, string deploymentId, bool verbose)
            {
                if (siloConfigFile == null)
                {
                    SiloConfigFile = "OrleansConfiguration.xml";
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(siloConfigFile))
                    {
                        throw new ArgumentException("Silo configuration path name is empty or whitespace");
                    }
                    else
                    {
                        SiloConfigFile = siloConfigFile;                        
                    }
                }

                if (siloName == null)
                {
                    SiloName = Dns.GetHostName();
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(siloName))
                    {
                        throw new ArgumentException("Silo name is empty or whitespace");
                    }
                    else
                    {
                        SiloName = siloName;                        
                    }
                }

                if (deploymentId != null)
                {
                    if (string.IsNullOrWhiteSpace(deploymentId))
                    {
                        throw new ArgumentException("Deployment ID is empty or whitespace");
                    }
                    else
                    {
                        DeploymentId = deploymentId;                        
                    }
                }

                Verbose = verbose;

                Init();
            }

            ~OrleansHostWrapper()
            {
                Dispose(false);
            }

            public bool Run()
            {
                bool ok = false;

                try
                {
                    _siloHost.InitializeOrleansSilo();

                    ok = _siloHost.StartOrleansSilo();

                    if (ok)
                    {
                        Console.WriteLine(string.Format("Successfully started Orleans silo '{0}' as a {1} node.", _siloHost.Name, _siloHost.Type));
                    }
                    else
                    {
                        throw new SystemException(string.Format("Failed to start Orleans silo '{0}' as a {1} node.", _siloHost.Name, _siloHost.Type));
                    }
                }
                catch (Exception exc)
                {
                    _siloHost.ReportStartupError(exc);
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
                    _siloHost.StopOrleansSilo();

                    Console.WriteLine(string.Format("Orleans silo '{0}' shutdown.", _siloHost.Name));
                }
                catch (Exception exc)
                {
                    _siloHost.ReportStartupError(exc);
                    var msg = string.Format("{0}:\n{1}\n{2}", exc.GetType().FullName, exc.Message, exc.StackTrace);
                    Console.WriteLine(msg);
                }

                return ok;
            }

            private void Init()
            { 
                _siloHost = new SiloHost(SiloName) { ConfigFileName = SiloConfigFile, DeploymentId = DeploymentId, Debug = Verbose };
                _siloHost.LoadOrleansConfig();
            }

            public void Dispose()
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }

            protected virtual void Dispose(bool dispose)
            {
                if (_siloHost != null)
                {
                    _siloHost.Dispose();
                }
            }
        }
    }
