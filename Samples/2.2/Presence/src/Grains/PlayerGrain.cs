using System;
using System.Threading.Tasks;
using Orleans;

namespace Presence.Grains
{
    /// <summary>
    /// Represents an individual player that may or may not be in a game at any point in time.
    /// </summary>
    public class PlayerGrain : Grain, IPlayerGrain
    {
        private IGameGrain currentGame;

        /// <summary>
        /// Game the player is currently in. May be null.
        /// </summary>
        public Task<IGameGrain> GetCurrentGame() => Task.FromResult(currentGame);

        /// <summary>
        /// Game grain calls this method to notify that the player has joined the game.
        /// </summary>
        public Task JoinGame(IGameGrain game)
        {
            currentGame = game;
            Console.WriteLine("Player {0} joined game {1}", this.GetPrimaryKey(), game.GetPrimaryKey());
            return Task.CompletedTask;
        }

        /// <summary>
        /// Game grain calls this method to notify that the player has left the game.
        /// </summary>
        public Task LeaveGame(IGameGrain game)
        {
            currentGame = null;
            Console.WriteLine("Player {0} left game {1}", this.GetPrimaryKey(), game.GetPrimaryKey());
            return Task.CompletedTask;
        }
    }
}
