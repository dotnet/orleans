using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
        private GameStatus status;
        private HashSet<IGameObserver> subscribers;
        private HashSet<Guid> players;

        public override Task OnActivateAsync()
        {
            subscribers = new HashSet<IGameObserver>();
            players = new HashSet<Guid>();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Presense grain calls this method to update the game with its latest status.
        /// </summary>
        public async Task UpdateGameStatus(GameStatus status)
        {
            this.status = status;

            // Check for new players that joined since last update
            foreach (var player in status.Players)
            {
                if (!players.Contains(player))
                {
                    try
                    {
                        // Here we call player grains serially, which is less efficient than a fan-out but simpler to express.
                        await GrainFactory.GetGrain<IPlayerGrain>(player).JoinGame(this.AsReference<IGameGrain>());
                        players.Add(player);
                    }
                    catch (Exception)
                    {
                        // Ignore exceptions while telling player grains to join the game. 
                        // Since we didn't add the player to the list, this will be tried again with next update.
                    }
                }
            }

            // Check for players that left the game since last update
            var promises = new List<Task>();
            foreach (var player in players)
            {
                if (!status.Players.Contains(player))
                {
                    try
                    {
                        // Here we do a fan-out with multiple calls going out in parallel. We join the promisses later.
                        // More code to write but we get lower latency when calling multiple player grains.
                        promises.Add(GrainFactory.GetGrain<IPlayerGrain>(player).LeaveGame(this.AsReference<IGameGrain>()));
                        players.Remove(player);
                    }
                    catch (Exception)
                    {
                        // Ignore exceptions while telling player grains to leave the game.
                        // Since we didn't remove the player from the list, this will be tried again with next update.
                    }
                }
            }

            // Joining promises
            await Task.WhenAll(promises);

            // Notify subscribers about the latest game score
            foreach (var subscriber in subscribers.ToArray())
            {
                try
                {
                    subscriber.UpdateGameScore(status.Score);
                }
                catch (Exception)
                {
                    subscribers.Remove(subscriber);
                }
            }

            return;
        }

        public Task SubscribeForGameUpdates(IGameObserver subscriber)
        {
            subscribers.Add(subscriber);
            return Task.CompletedTask;
        }

        public Task UnsubscribeForGameUpdates(IGameObserver subscriber)
        {
            subscribers.Remove(subscriber);
            return Task.CompletedTask;
        }
    }
}
