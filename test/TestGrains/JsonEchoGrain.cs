using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Orleans;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class JsonEchoGrain : Grain, IJsonEchoGrain
    {
        public Task Ping()
        {
            return Task.CompletedTask;
        }

        public Task<JObject> EchoJson(JObject data)
        {
            return Task.FromResult(data);
        }
    }
}
