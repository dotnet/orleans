using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    /// <summary>
    /// Echo grain to test round trip of JSON data.
    /// </summary>
    public interface IJsonEchoGrain : IGrainWithIntegerKey
    {
        Task Ping();
        Task<JObject> EchoJson(JObject data);
    }
}
