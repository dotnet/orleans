using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Samples.Presence.GrainInterfaces;

namespace PresenceGrains
{
    /// <summary>
    /// Represents an individual player that may or may not be in a game at any point in time
    /// </summary>
    public class PlayerGrain : Grain, IPlayerGrain
    {
        private IGameGrain currentGame;

        public override Task OnActivateAsync()
        {
            currentGame = null;
            return TaskDone.Done;
        }

        // Game the player is currently in. May be null.
        public Task<IGameGrain> GetCurrentGame()
        {
            return Task.FromResult(currentGame);
        }

        // Game grain calls this method to notify that the player has joined the game.
        public Task JoinGame(IGameGrain game)
        {
            currentGame = game;
            Console.WriteLine("Player {0} joined game {1}", this.GetPrimaryKey(), game.GetPrimaryKey());
            return TaskDone.Done;
        }

        // Game grain calls this method to notify that the player has left the game.
        public Task LeaveGame(IGameGrain game)
        {
            currentGame = null;
            Console.WriteLine("Player {0} left game {1}", this.GetPrimaryKey(), game.GetPrimaryKey());
            return TaskDone.Done;
        }
    }
}
