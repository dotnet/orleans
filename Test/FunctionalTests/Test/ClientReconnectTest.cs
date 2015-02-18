using System;
using Orleans;
using Orleans.Runtime.Host;

namespace UnitTests.Apps
{
    public class ClientReconnectTest
    {
        public static void Run(int count = 1000)
        {
            for (int n = 0; n < count; n++)
            {
                AzureClient.Initialize();
                //Console.WriteLine(GrainClient.Current.CurrentGateway);
                AzureClient.Uninitialize();
            }
        }
    }
}
