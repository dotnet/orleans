using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;

namespace Presence.Grains
{
    /// <summary>
    /// Represents an individual player that may or may not be in a game at any point in time.
    /// </summary>
    public class PlayerGrain : Grain, IPlayerGrain
    {
        private readonly ILogger<PlayerGrain> _logger;
        private IGameGrain _currentGame;

        public PlayerGrain(ILogger<PlayerGrain> logger)
        {
            _logger = logger;
        }

        private Guid GrainKey => this.GetPrimaryKey();

        /// <summary>
        /// Game the player is currently in. May be null.
        /// </summary>
        public Task<IGameGrain> GetCurrentGameAsync() => Task.FromResult(_currentGame);

        /// <summary>
        /// Game grain calls this method to notify that the player has joined the game.
        /// </summary>
        public Task JoinGameAsync(IGameGrain game)
        {
            _currentGame = game;
            _logger.LogInformation("Player {PlayerKey} joined game {GameKey}", GrainKey, game.GetPrimaryKey());
            return Task.CompletedTask;
        }

        /// <summary>
        /// Game grain calls this method to notify that the player has left the game.
        /// </summary>
        public Task LeaveGameAsync(IGameGrain game)
        {
            _currentGame = null;
            _logger.LogInformation("Player {PlayerKey} left game {GameKey}", GrainKey, game.GetPrimaryKey());
            return Task.CompletedTask;
        }
    }
}
