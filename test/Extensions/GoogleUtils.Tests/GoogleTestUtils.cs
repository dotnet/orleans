using System;
using System.Net.Http;

namespace GoogleUtils.Tests
{
    public static class GoogleTestUtils
    {
        public static string DeploymentId => Guid.NewGuid().ToString();
        public static string ProjectId => Environment.GetEnvironmentVariable("PUBSUB_PROJECT_ID") ?? "feisty-flow-173313";
        public static string TopicId => "GPSTestUtilsTopicId";
        public static string EmulatorAddress => Environment.GetEnvironmentVariable("PUBSUB_EMULATOR_HOST");

        public static Lazy<bool> IsPubSubSimulatorAvailable = new Lazy<bool>(() => 
        {
            try
            {
                var parts = EmulatorAddress.Split(":");
                using var client = new System.Net.Sockets.TcpClient();
                client.Connect(parts[0], int.Parse(parts[1]));
                return true;
            }
            catch
            {
                return false;
            }
        });
    }
}
