using System;
using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;

using Orleans.Runtime.Configuration;
using Orleans.Runtime.Host;

using LoadTestGrainInterfaces;

namespace LoadTestBase
{
    public abstract class OrleansClientWorkerBase : DirectClientWorkerBase
    {        
        protected bool _useAzureSiloTable;

        public void InitializeOrleansClientConnection(IPEndPoint gateway, int instanceIndex, bool useAzureSiloTable)
        {
            _useAzureSiloTable = useAzureSiloTable;
            
            if (!Orleans.GrainClient.IsInitialized)
            {
                ClientConfiguration.ClientName = "Client_" + Dns.GetHostName() + "_" + _workerIndex;
                ClientConfiguration config = ClientConfiguration.StandardLoad();

                if (_useAzureSiloTable)
                {
                    AzureClient.Initialize(config);
                }
                else 
                {
                    if (instanceIndex >= 0)
                    {
                        // Use specified silo index from config file for GW selection.
                        config.PreferedGatewayIndex = instanceIndex % config.Gateways.Count;
                    }
                    else if (gateway != null)
                    {
                        // Use specified gateway address passed on command line
                        if (!config.Gateways.Contains(gateway))
                        {
                            config.Gateways.Add(gateway);
                        }
                        config.PreferedGatewayIndex = config.Gateways.IndexOf(gateway);
                    }
                    // Else just use standard config from file

                    Orleans.GrainClient.Initialize(config);
                }
            }
        }

        public override void Uninitialize()
        {
            try
            {
                base.Uninitialize();
                if (Orleans.GrainClient.IsInitialized)
                {
                    if (_useAzureSiloTable)
                    {
                        AzureClient.Uninitialize();
                    }
                    else
                    {
                        Orleans.GrainClient.Uninitialize();
                    }
                }
            }
            catch (Exception exc)
            {
                base.WriteProgress("OrleansClientWorkerBase.Uninitialize() has throw an exception: {0}", exc);
            }
        }

        public void WriteProgress(string format, object[] args, bool bulk)
        {
            if (Orleans.GrainClient.IsInitialized)
            {
                Orleans.GrainClient.Logger.Info(bulk ? 1 : 0, format, args); // use log code 1. No code defaults to log code 0, which is not bulked.
            }
            else
            {
                base.WriteProgress(format, args);
            }
        }

        public override void WriteProgress(string format, params object[] args)
        {
            WriteProgress(format, args, true);
        }

        public void WriteProgressWithoutBulking(string format, params object[] args)
        {
            WriteProgress(format, args, false);
        }

        public void WriteProgress(int logCode, string format, params object[] args)
        {
            if (Orleans.GrainClient.IsInitialized)
            {
                Orleans.GrainClient.Logger.Info(logCode, format, args);
            }
            else
            {
                base.WriteProgress(format, args);
            }
        }

        protected async Task WaitAtStartBarrier(int barrierSize)
        {
            if (barrierSize < 1)
            {
                throw new ArgumentOutOfRangeException("barrierSize", barrierSize, "Start barrier size is less than 1.");
            }

            if (_workerIndex != 0)
            {
                throw new NotSupportedException("The barrier doesn't currently support multiple workers.");
            }

            string myPollerId = Guid.NewGuid().ToString();
            IBarrierGrain barrierGrain = BarrierGrainFactory.GetGrain(0);

            Stopwatch stopwatch = Stopwatch.StartNew();
            while (!(await barrierGrain.IsReady(barrierSize, myPollerId)))
            {
                WriteProgressWithoutBulking(string.Format("Waiting for barrier ({0} sec)...", stopwatch.Elapsed.TotalSeconds));
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
            stopwatch.Stop();
            WriteProgressWithoutBulking(string.Format("Start barrier says go! ({0} sec).", stopwatch.Elapsed.TotalSeconds));
        }
    }
}