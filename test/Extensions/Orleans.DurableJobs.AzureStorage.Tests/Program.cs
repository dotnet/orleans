using Orleans.TestingHost;

namespace Tester.AzureUtils.DurableJobs;

public static class Program
{
    public static async Task Main(string[] args) => await StandaloneSiloHost.Main(args);
}
