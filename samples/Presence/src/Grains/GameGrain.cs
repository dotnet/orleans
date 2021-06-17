using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Presence.Grains.Models;

namespace Presence.Grains
{
    /// <summary>
    /// Represents a game in progress and holds the game's state in memory.
    /// Notifies player grains about their players joining and leaving the game.
    /// Updates subscribed observers about the progress of the game.
    /// </summary>
    public class GameGrain : Grain, IGameGrain
    {
        private readonly ILogger<GameGrain> _logger;
        private readonly HashSet<IGameObserver> _observers = new();
        private readonly HashSet<Guid> _players = new();
        private GameStatus _status = GameStatus.Empty;

        public GameGrain(ILogger<GameGrain> logger)
        {
            _logger = logger;
        }

        private Guid GrainKey => this.GetPrimaryKey();

        /// <summary>
        /// Presense grain calls this method to update the game with its latest status.
        /// </summary>
        public async Task UpdateGameStatusAsync(GameStatus status)
        {
            _status = status;

            // Check for new players that joined since last update
            foreach (var player in _status.PlayerKeys)
            {
                if (!_players.Contains(player))
                {
                    try
                    {
                        // Here we call player grains serially, which is less efficient than a fan-out but simpler to express.
                        await GrainFactory.GetGrain<IPlayerGrain>(player).JoinGameAsync(this.AsReference<IGameGrain>());
                        _players.Add(player);
                    }
                    catch (Exception error)
                    {
                        // Ignore exceptions while telling player grains to join the game. 
                        // Since we didn't add the player to the list, this will be tried again with next update.
                        _logger.LogWarning(error, "Failed to tell player {PlayerKey} to join game {GameKey}", player, GrainKey);
                    }
                }
            }

            // Check for players that left the game since last update
            var promises = new List<Tuple<Guid, Task>>();
            foreach (var player in _players)
            {
                if (!_status.PlayerKeys.Contains(player))
                {
                    // Here we do a fan-out with multiple calls going out in parallel. We join the promisses later.
                    // More code to write but we get lower latency when calling multiple player grains.
                    promises.Add(Tuple.Create(player, GrainFactory.GetGrain<IPlayerGrain>(player).LeaveGameAsync(this.AsReference<IGameGrain>())));
                }
            }

            // Joining promises
            foreach (var promise in promises)
            {
                try
                {
                    await promise.Item2;
                    _players.Remove(promise.Item1);
                }
                catch (Exception error)
                {
                    _logger.LogWarning(error, "Failed to tell player {PlayerKey} to leave the game {GameKey}", promise.Item1, GrainKey);
                }
            }

            // Notify observers about the latest game score
            List<IGameObserver> failed = null;
            foreach (var observer in _observers)
            {
                try
                {
                    observer.UpdateGameScore(_status.Score);
                }
                catch (Exception error)
                {
                    _logger.LogWarning(error, "Failed to notify observer {ObserverKey} of score for game {GameKey}. Removing observer.", observer.GetPrimaryKey(), GrainKey);

                    // add observer to a list of failures
                    // however defer failed list creation until necessary to avoid incurring an allocation on every call
                    if (failed == null)
                    {
                        failed = new List<IGameObserver>();
                    }

                    failed.Add(observer);
                }
            }

            // Remove dead observers
            if (failed != null)
            {
                foreach (var observer in failed)
                {
                    _observers.Remove(observer);
                }
            }

            return;
        }

        public Task ObserveGameUpdatesAsync(IGameObserver observer)
        {
            _observers.Add(observer);
            return Task.CompletedTask;
        }

        public Task UnobserveGameUpdatesAsync(IGameObserver observer)
        {
            _observers.Remove(observer);
            return Task.CompletedTask;
        }
    }
}
