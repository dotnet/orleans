using System.Threading.Tasks;
using Orleans;
using Presence.Grains.Models;

namespace Presence.Grains
{
    /// <summary>
    /// Interface to a specific instance of a game with its own status and list of players.
    /// </summary>
    public interface IGameGrain : IGrainWithGuidKey
    {
        Task UpdateGameStatusAsync(GameStatus status);
        Task SubscribeForGameUpdatesAsync(IGameObserver observer);
        Task UnsubscribeForGameUpdatesAsync(IGameObserver observer);
    }
}
