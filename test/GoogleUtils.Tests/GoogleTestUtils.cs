using System;
using System.Net.Http;

namespace GoogleUtils.Tests
{
    public static class GoogleTestUtils
    {
        public static string DeploymentId => Guid.NewGuid().ToString();
        public static string ProjectId => "feisty-flow-173313";
        public static string TopicId => "GPSTestUtilsTopicId";

        public static Lazy<bool> IsPubSubSimulatorAvailable = new Lazy<bool>(() => 
        {
            try
            {
                var ok = new HttpClient().GetAsync("http://localhost:8085").Result;
                return ok.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        });
    }
}
