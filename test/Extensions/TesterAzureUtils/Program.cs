using Orleans.TestingHost;

namespace Tester.AzureUtils.TestSilo
{
    public static class Program 
    {
        public static async Task Main(string[] args) => await StandaloneSiloHost.Main(args);
    }
}
