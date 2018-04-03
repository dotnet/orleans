using System;
using System.Threading.Tasks;

namespace OrleansXO.GrainInterfaces
{

    public interface IPairingGrain : Orleans.IGrainWithIntegerKey
    {
        Task AddGame(Guid gameId, string name);
        Task RemoveGame(Guid gameId);
        Task<PairingSummary[]> GetGames();
    }

    public class PairingSummary
    {
        public Guid GameId { get; set; }
        public string Name { get; set; }
    }



}
