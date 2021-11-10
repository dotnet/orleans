using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;

namespace Distributed.Client.Scenarios
{
    public interface IScenario<T>
    {
        string Name { get; }

        List<Option> Options { get; }

        Task Initialize(IClusterClient client, T parameter, ILogger logger);

        Task IssueRequest(int request);

        Task Cleanup();
    }
}
