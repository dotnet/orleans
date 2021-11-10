using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using VotingContract;

namespace Voting.Data
{
    public partial class DemoService
    {
        private readonly IGrainFactory _grainFactory;
        private readonly ILogger<DemoService> _logger;

        public DemoService(IGrainFactory grainFactory, ILogger<DemoService> logger)
        {
            _grainFactory = grainFactory;
            _logger = logger;
        }

        public async Task SimulateVoters(string pollId, int numVotes)
        {
            try
            {
                var pollGrain = _grainFactory.GetGrain<IPollGrain>(pollId);
                var results = await pollGrain.GetCurrentResults();
                var random = new Random();
                while (numVotes-- > 0)
                {
                    var optionId = random.Next(0, results.Options.Count);
                    await pollGrain.AddVote(optionId);

                    // Wait some time.
                    await Task.Delay(random.Next(100, 1000));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while simulating voters");
            }
        }
    }
}