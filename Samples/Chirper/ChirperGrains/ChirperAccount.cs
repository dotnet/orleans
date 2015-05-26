﻿//*********************************************************//
//    Copyright (c) Microsoft. All rights reserved.
//    
//    Apache 2.0 License
//    
//    You may obtain a copy of the License at
//    http://www.apache.org/licenses/LICENSE-2.0
//    
//    Unless required by applicable law or agreed to in writing, software 
//    distributed under the License is distributed on an "AS IS" BASIS, 
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or 
//    implied. See the License for the specific language governing 
//    permissions and limitations under the License.
//
//*********************************************************

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Concurrency;
using Orleans.Samples.Chirper.GrainInterfaces;
using Orleans.Providers;

namespace Orleans.Samples.Chirper.Grains
{
    public interface IChirperAccountState : IGrainState
    {
        /// <summary>The list of publishers who this user is following</summary>
        Dictionary<ChirperUserInfo, IChirperPublisher> Subscriptions { get; set; }

        /// <summary>The list of subscribers who are following this user</summary>
        Dictionary<ChirperUserInfo, IChirperSubscriber> Followers { get; set; }

        /// <summary>Size for the recently received message cache</summary>
        int ReceivedMessagesCacheSize { get; set; }

        /// <summary>Size for the published message cache</summary>
        int PublishedMessagesCacheSize { get; set; }

        /// <summary>Chirp messages recently received by this user</summary>
        Queue<ChirperMessage> RecentReceivedMessages { get; set; }

        /// <summary>Chirp messages recently published by this user</summary>
        Queue<ChirperMessage> MyPublishedMessages { get; set; }

        long UserId { get; set; }

        /// <summary>Alias / username for this actor / user</summary>
        string UserAlias { get; set;  }
    }

    [Reentrant]
    [StorageProvider(ProviderName = "MemoryStore")]
    public class ChirperAccount : Grain<IChirperAccountState>, IChirperAccount
    {
        [NonSerialized]
        private ObserverSubscriptionManager<IChirperViewer> viewers;

        [NonSerialized]
        private Logger logger;

        private const int MAX_MESSAGE_LENGTH = 280;

        private string Me
        {
            get
            {
                return String.Format("I am: [{0}.{1}]", State.UserAlias, State.UserId);
            }
        }

        #region Grain overrides

        public override Task OnActivateAsync()
        {
            State.ReceivedMessagesCacheSize = 100;
            State.PublishedMessagesCacheSize = 100;
            State.RecentReceivedMessages = new Queue<ChirperMessage>(State.ReceivedMessagesCacheSize);
            State.MyPublishedMessages = new Queue<ChirperMessage>(State.PublishedMessagesCacheSize);
            State.Followers = new Dictionary<ChirperUserInfo, IChirperSubscriber>();
            State.Subscriptions = new Dictionary<ChirperUserInfo, IChirperPublisher>();

            this.logger = this.GetLogger("ChirperAccountGrain");

            State.UserId = this.GetPrimaryKeyLong();

            if (logger.IsVerbose) logger.Verbose("{0}: Created activation of ChirperAccount grain.", Me);

            this.viewers = new ObserverSubscriptionManager<IChirperViewer>();
            // Viewers are transient connections -- they will need to reconnect themselves

            return TaskDone.Done;
        }

        #endregion

        #region IChirperAccountGrain interface methods

        public Task SetUserDetails(ChirperUserInfo userInfo)
        {
            string alias = userInfo.UserAlias;
            if (alias != null)
            {
                if (logger.IsVerbose)
                    logger.Verbose("{0} Setting UserAlias = {1}.", Me, alias);
                State.UserAlias = alias;
            }
            return TaskDone.Done;
        }

        public async Task PublishMessage(string message)
        {
            ChirperMessage chirp = CreateNewChirpMessage(message);

            if (logger.IsVerbose)
                logger.Verbose("{0} Publishing new chirp message = {1}.", Me, chirp);

            State.MyPublishedMessages.Enqueue(chirp);

            // only relevant when not using fixed queue
            while (State.MyPublishedMessages.Count > State.PublishedMessagesCacheSize) // to keep not more than the max number of messages
            {
                State.MyPublishedMessages.Dequeue();
            }

            List<Task> promises = new List<Task>();

            if (State.Followers.Count > 0)
            {
                // Notify any subscribers that a new chirp has published
                if (logger.IsVerbose)
                    logger.Verbose("{0} Sending new chirp message to {1} subscribers.", Me, State.Followers.Count);

                foreach (IChirperSubscriber subscriber in State.Followers.Values)
                {
                    promises.Add(subscriber.NewChirp(chirp));
                }
            }

            if (viewers.Count > 0)
            {
                // Notify any viewers that a new chirp has published
                if (logger.IsVerbose) logger.Verbose("{0} Sending new chirp message to {1} viewers.", Me, viewers.Count);
                viewers.Notify(
                    v => v.NewChirpArrived(chirp)
                );
            }

            await Task.WhenAll(promises.ToArray());
        }
        public Task<List<ChirperMessage>> GetReceivedMessages(int n, int start)
        {
            if (start < 0) start = 0;
            if ((start + n) > State.RecentReceivedMessages.Count)
            {
                n = State.RecentReceivedMessages.Count - start;
            }
            return Task.FromResult(
                State.RecentReceivedMessages.Skip(start).Take(n).ToList());
        }
        public async Task FollowUserId(long userId)
        {
            if (logger.IsVerbose) logger.Verbose("{0} FollowUserId({1}).", Me, userId);
            IChirperPublisher userToFollow = ChirperPublisherFactory.GetGrain(userId);
            string alias = await userToFollow.GetUserAlias();
            await FollowUser(userId, alias, userToFollow);
        }
        public async Task UnfollowUserId(long userId)
        {
            if (logger.IsVerbose) logger.Verbose("{0} UnfollowUserId({1}).", Me, userId);
            IChirperPublisher userToUnfollow = ChirperPublisherFactory.GetGrain(userId);
            string alias = await userToUnfollow.GetUserAlias();
            await UnfollowUser(userId, alias, userToUnfollow);
        }
        public Task<List<ChirperUserInfo>> GetFollowingList()
        {
            return Task.FromResult(State.Subscriptions.Keys.ToList());
        }
        public Task<List<ChirperUserInfo>> GetFollowersList()
        {
            return Task.FromResult(State.Followers.Keys.ToList());
        }
        public Task ViewerConnect(IChirperViewer viewer)
        {
            viewers.Subscribe(viewer);
            return TaskDone.Done;
        }
        public Task ViewerDisconnect(IChirperViewer viewer)
        {
            viewers.Unsubscribe(viewer);
            return TaskDone.Done;
        }
        #endregion

        #region IChirperPublisher interface methods

        public Task<long> GetUserId()
        {
            return Task.FromResult(State.UserId);
        }

        public Task<string> GetUserAlias()
        {
            return Task.FromResult(State.UserAlias);
        }

        public Task<List<ChirperMessage>> GetPublishedMessages(int n, int start)
        {
            if (start < 0) start = 0;
            if ((start + n) > State.MyPublishedMessages.Count) n = State.MyPublishedMessages.Count - start;
            return Task.FromResult(
                State.MyPublishedMessages.Skip(start).Take(n).ToList());
        }

        public Task AddFollower(string alias, long userId, IChirperSubscriber follower)
        {
            ChirperUserInfo userInfo = ChirperUserInfo.GetUserInfo(userId, alias);
            if (State.Followers.ContainsKey(userInfo))
            {
                State.Followers.Remove(userInfo);
            }
            State.Followers[userInfo] = follower;
            return TaskDone.Done;
        }

        public Task RemoveFollower(string alias, IChirperSubscriber follower)
        {
            IEnumerable<KeyValuePair<ChirperUserInfo, IChirperSubscriber>> found = State.Followers.Where(f => f.Key.UserAlias == alias).ToList();
            if (found.Any())
            {
                ChirperUserInfo userInfo = found.FirstOrDefault().Key;
                State.Followers.Remove(userInfo);
            }
            return TaskDone.Done;
        }

        #endregion

        #region IChirperSubscriber notification callback interface

        public Task NewChirp(ChirperMessage chirp)
        {
            if (logger.IsVerbose) logger.Verbose("{0} Received chirp message = {1}", Me, chirp);

            State.RecentReceivedMessages.Enqueue(chirp);

            // only relevant when not using fixed queue
            while (State.MyPublishedMessages.Count > State.PublishedMessagesCacheSize) // to keep not more than the max number of messages
            {
                State.MyPublishedMessages.Dequeue();
            }

            if (viewers.Count > 0)
            {
                // Notify any viewers that a new chirp has published
                if (logger.IsVerbose)
                    logger.Verbose("{0} Sending received chirp message to {1} viewers", Me, viewers.Count);
                viewers.Notify(
                    v => v.NewChirpArrived(chirp)
                );
            }

#if DEBUG
            const string busywait = "#busywait";
            var i = chirp.Message.IndexOf(busywait, StringComparison.Ordinal);
            int n;
            if (i >= 0 && Int32.TryParse(chirp.Message.Substring(i + busywait.Length + 1), out n))
            {
                var watch = new Stopwatch();
                watch.Start();
                while (watch.ElapsedMilliseconds < n)
                {
                    // spin
                }
                watch.Stop();
            }
#endif

            return TaskDone.Done;
        }
        #endregion


        private async Task FollowUser(long userId, string userAlias, IChirperPublisher userToFollow)
        {
            if (logger.IsVerbose) logger.Verbose("{0} FollowUser({1}).", Me, userAlias);
            await userToFollow.AddFollower(State.UserAlias, State.UserId, this);

            ChirperUserInfo userInfo = ChirperUserInfo.GetUserInfo(userId, userAlias);
            State.Subscriptions[userInfo] = userToFollow;
            
            // Notify any viewers that a subscription has been added for this user
            viewers.Notify(
                v => v.SubscriptionAdded(userInfo)
            );
        }
        private async Task UnfollowUser(long userId, string userAlias, IChirperPublisher userToUnfollow)
        {
            await userToUnfollow.RemoveFollower(State.UserAlias, this);

            ChirperUserInfo userInfo = ChirperUserInfo.GetUserInfo(userId, userAlias);
            State.Subscriptions.Remove(userInfo);

            // Notify any viewers that a subscription has been removed for this user
            viewers.Notify(
                v => v.SubscriptionRemoved(userInfo)
            );
        }
        private ChirperMessage CreateNewChirpMessage(string message)
        {
            ChirperMessage chirp = new ChirperMessage();
            chirp.PublisherId = State.UserId;
            chirp.PublisherAlias = State.UserAlias;
            chirp.Timestamp = DateTime.Now;
            chirp.Message = message;
            if (chirp.Message.Length > MAX_MESSAGE_LENGTH) chirp.Message = message.Substring(0, MAX_MESSAGE_LENGTH);
            return chirp;
        }
    }
}
