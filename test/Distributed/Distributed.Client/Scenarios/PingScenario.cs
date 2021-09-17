using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Distributed.GrainInterfaces;
using Distributed.GrainInterfaces.Ping;
using Orleans;

namespace Distributed.Client.Scenarios
{
    public class PingScenario : IScenario<PingScenario.Parameters>
    {
        public string Name => "ping";

        private IClusterClient _clusterClient;
        private List<(IPingRouterGrain Router, Guid[] Targets)> _requestList;

        public List<Option> Options => new()
        {
            OptionHelper.CreateOption<int>("--routers", defaultValue: 1000),
            OptionHelper.CreateOption<int>("--targetsPerRouter", defaultValue: 10),
            OptionHelper.CreateOption<bool>("--doWarmup", defaultValue: true),
        };

        public async Task Initialize(IClusterClient clusterClient, Parameters p)
        {
            _clusterClient = clusterClient;
            _requestList = new List<(IPingRouterGrain Router, Guid[] Targets)>(p.Routers);

            for (var i = 0; i < p.Routers; i++)
            {
                var targetIds = new Guid[p.TargetsPerRouter];
                for (var j = 0; j < p.TargetsPerRouter; j++)
                {
                    targetIds[j] = Guid.NewGuid();
                }

                var routerGrain = _clusterClient.GetGrain<IPingRouterGrain>(Guid.NewGuid());

                if (p.DoWarmup)
                {
                    await routerGrain.Ping(targetIds);
                }

                _requestList.Add((routerGrain, targetIds));
            }
        }

        public async Task IssueRequest(int request)
        {
            var (router, targets) = _requestList[request % _requestList.Count];
            await router.Ping(targets);
        }

        public Task Cleanup() => Task.CompletedTask;

        public class Parameters
        {
            public int Routers { get; set; }
            public int TargetsPerRouter { get; set; }
            public bool DoWarmup { get; set; }
        }
    }
}
