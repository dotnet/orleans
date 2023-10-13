using Orleans.TestingHost;

namespace TestVersionGrains
{
    public static class Program 
    {
        public static async Task Main(string[] args) => await StandaloneSiloHost.Main(args);
    }
}
