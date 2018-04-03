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
    /// Represents a game in progress and holds the game's state in memory.
    /// Notifies player grains about their players joining and leaving the game.
    /// Updates subscribed observers about the progress of the game
    /// </summary>
    public class GameGrain : Grain, IGameGrain
    {
        private GameStatus status;
        private ObserverSubscriptionManager<IGameObserver> subscribers;
        private HashSet<Guid> players;

        public override Task OnActivateAsync()
        {
            subscribers = new ObserverSubscriptionManager<IGameObserver>();
            players = new HashSet<Guid>();
            return TaskDone.Done;
        }

        public override Task OnDeactivateAsync()
        {
            subscribers.Clear();
            players.Clear();
            return TaskDone.Done;
        }

        /// <summary>
        /// Presense grain calls this method to update the game with its latest status
        /// </summary>
        public async Task UpdateGameStatus(GameStatus status)
        {
            this.status = status;

            // Check for new players that joined since last update
            foreach(Guid player in status.Players)
                if(!players.Contains(player))
                {
                    try
                    {
                        // Here we call player grains serially, which is less efficient than a fan-out but simpler to express.
                        await GrainFactory.GetGrain<IPlayerGrain>(player).JoinGame(this);
                        players.Add(player);
                    }
                    catch (Exception)
                    {
                        // Ignore exceptions while telling player grains to join the game. 
                        // Since we didn't add the player to the list, this will be tried again with next update.
                    }
                }

            // Check for players that left the game since last update
            List<Task> promises = new List<Task>();
            foreach(Guid player in players)
                if (!status.Players.Contains(player))
                {
                    try
                    {
                        // Here we do a fan-out with multiple calls going out in parallel. We join the promisses later.
                        // More code to write but we get lower latency when calling multiple player grains.
                        promises.Add(GrainFactory.GetGrain<IPlayerGrain>(player).LeaveGame(this));
                        players.Remove(player);
                    }
                    catch (Exception)
                    {
                        // Ignore exceptions while telling player grains to leave the game.
                        // Since we didn't remove the player from the list, this will be tried again with next update.
                    }
                }

            // Joining promises
            await Task.WhenAll(promises);

            // Notify subsribers about the latest game score
            subscribers.Notify((s) => s.UpdateGameScore(status.Score)); 

            return;
        }

        public Task SubscribeForGameUpdates(IGameObserver subscriber)
        {
            subscribers.Subscribe(subscriber);
            return TaskDone.Done;
        }

        public Task UnsubscribeForGameUpdates(IGameObserver subscriber)
        {
            subscribers.Unsubscribe(subscriber);
            return TaskDone.Done;
        }
    }
}
