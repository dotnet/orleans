using System.Threading.Tasks;
using FasterSample.Grains;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Hosting;

namespace FasterSample.SiloHost
{
    public static class Program
    {
        public static async Task Main()
        {
            await Host
                .CreateDefaultBuilder()
                .UseOrleans(orleans => orleans
                    .ConfigureApplicationParts(manager => manager.AddApplicationPart(typeof(DictionaryFrequencyGrain).Assembly).WithReferences())
                    .UseLocalhostClustering())
                .RunConsoleAsync()
                .ConfigureAwait(false);
        }
    }
}