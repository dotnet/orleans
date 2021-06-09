using System;
using System.Linq;
using System.Runtime.Caching;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;

namespace TicTacToe.Grains
{
    [Reentrant]
    public class PairingGrain : Grain, IPairingGrain
    {
        private readonly MemoryCache _cache = new("pairing");

        public Task AddGame(Guid gameId, string name)
        {
            _cache.Add(gameId.ToString(), name, new DateTimeOffset(DateTime.UtcNow).AddHours(1));
            return Task.CompletedTask;
        }

        public Task RemoveGame(Guid gameId)
        {
            _cache.Remove(gameId.ToString());
            return Task.CompletedTask;
        }

        public Task<PairingSummary[]> GetGames() => Task.FromResult(_cache.Select(x => new PairingSummary { GameId = Guid.Parse(x.Key), Name = x.Value as string }).ToArray());

    }
}
