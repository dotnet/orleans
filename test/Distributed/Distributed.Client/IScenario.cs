using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Threading.Tasks;
using Orleans;

namespace Distributed.Client
{
    public interface IScenario<T>
    {
        string Name { get; }

        List<Option> Options { get; }

        Task Initialize(IClusterClient client, T parameter);

        Task IssueRequest(int request);

        Task Cleanup();
    }
}
