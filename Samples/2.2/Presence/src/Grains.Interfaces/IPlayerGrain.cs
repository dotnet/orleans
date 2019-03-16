using System.Threading.Tasks;
using Orleans;

namespace Presence.Grains
{
    /// <summary>
    /// Interface to an individual player that may or may not be in a game at any point in time.
    /// </summary>
    public interface IPlayerGrain : IGrainWithGuidKey
    {
        Task<IGameGrain> GetCurrentGame();
        Task JoinGame(IGameGrain game);
        Task LeaveGame(IGameGrain game);
    }
}
