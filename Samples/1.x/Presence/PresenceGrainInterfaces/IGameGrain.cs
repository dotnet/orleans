using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;

namespace Orleans.Samples.Presence.GrainInterfaces
{
    /// <summary>
    /// Interface to a specific instance of a game with its own status and list of players
    /// </summary>
    public interface IGameGrain : IGrainWithGuidKey
    {
        Task UpdateGameStatus(GameStatus status);
        Task SubscribeForGameUpdates(IGameObserver subscriber);
        Task UnsubscribeForGameUpdates(IGameObserver subscriber);
    }

    public static class GameConstants
    {
        public static Guid NoGame = Guid.Empty;
    }
}
