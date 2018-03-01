using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;

namespace Orleans.Samples.Presence.GrainInterfaces
{
    /// <summary>
    /// Interface to an individual player that may or may not be in a game at any point in time
    /// </summary>
    public interface IPlayerGrain : IGrainWithGuidKey
    {
        Task<IGameGrain> GetCurrentGame();

        Task JoinGame(IGameGrain game);
        Task LeaveGame(IGameGrain game);
    }
}
