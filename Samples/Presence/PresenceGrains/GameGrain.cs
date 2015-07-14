/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

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
                        await PlayerGrainFactory.GetGrain(player).JoinGame(this);
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
                        promises.Add(PlayerGrainFactory.GetGrain(player).LeaveGame(this));
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
