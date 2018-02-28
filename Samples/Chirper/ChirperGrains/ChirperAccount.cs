using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Concurrency;
using Orleans.Samples.Chirper.GrainInterfaces;
using Orleans.Providers;

namespace Orleans.Samples.Chirper.Grains
{
    public class ChirperAccountState
    {
        /// <summary>The list of publishers who this user is following</summary>
        public Dictionary<ChirperUserInfo, IChirperPublisher> Subscriptions { get; set; }

        /// <summary>The list of subscribers who are following this user</summary>
        public Dictionary<ChirperUserInfo, IChirperSubscriber> Followers { get; set; }

        /// <summary>Chirp messages recently received by this user</summary>
        public Queue<ChirperMessage> RecentReceivedMessages { get; set; }

        /// <summary>Chirp messages recently published by this user</summary>
        public Queue<ChirperMessage> MyPublishedMessages { get; set; }

        public long UserId { get; set; }

        /// <summary>Alias / username for this actor / user</summary>
        public string UserAlias { get; set; }
    }

    [Reentrant]
    [StorageProvider(ProviderName = "MemoryStore")]
    public class ChirperAccount : Grain<ChirperAccountState>, IChirperAccount
    {
        /// <summary>Size for the recently received message cache</summary>
        private int ReceivedMessagesCacheSize;

        /// <summary>Size for the published message cache</summary>
        private int PublishedMessagesCacheSize;
        private ObserverSubscriptionManager<IChirperViewer> viewers;
        private Logger logger;
        private Task outstandingWriteStateOperation;

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
            ReceivedMessagesCacheSize = 100;
            PublishedMessagesCacheSize = 100;
            if (State.RecentReceivedMessages == null)
            {
                State.RecentReceivedMessages = new Queue<ChirperMessage>(ReceivedMessagesCacheSize);
            }
            if (State.MyPublishedMessages == null)
            {
                State.MyPublishedMessages = new Queue<ChirperMessage>(PublishedMessagesCacheSize);
            }
            if (State.Followers == null)
            {
                State.Followers = new Dictionary<ChirperUserInfo, IChirperSubscriber>();
            }
            if (State.Subscriptions == null)
            {
                State.Subscriptions = new Dictionary<ChirperUserInfo, IChirperPublisher>();
            }

            State.UserId = this.GetPrimaryKeyLong();

            logger = GetLogger("ChirperAccountGrain");

            if (logger.IsVerbose) logger.Verbose("{0}: Created activation of ChirperAccount grain.", Me);

            viewers = new ObserverSubscriptionManager<IChirperViewer>();
            // Viewers are transient connections -- they will need to reconnect themselves
            return TaskDone.Done;
        }

        #endregion

        #region IChirperAccountGrain interface methods

        public async Task SetUserDetails(ChirperUserInfo userInfo)
        {
            string alias = userInfo.UserAlias;
            if (alias != null)
            {
                if (logger.IsVerbose)
                    logger.Verbose("{0} Setting UserAlias = {1}.", Me, alias);
                State.UserAlias = alias;
                await WriteStateAsync();
            }
        }

        public async Task PublishMessage(string message)
        {
            ChirperMessage chirp = CreateNewChirpMessage(message);

            if (logger.IsVerbose)
                logger.Verbose("{0} Publishing new chirp message = {1}.", Me, chirp);

            State.MyPublishedMessages.Enqueue(chirp);

            // only relevant when not using fixed queue
            while (State.MyPublishedMessages.Count > PublishedMessagesCacheSize) // to keep not more than the max number of messages
            {
                State.MyPublishedMessages.Dequeue();
            }

            await WriteStateAsync();

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
            IChirperPublisher userToFollow = GrainFactory.GetGrain<IChirperPublisher>(userId);
            string alias = await userToFollow.GetUserAlias();
            await FollowUser(userId, alias, userToFollow);
        }
        public async Task UnfollowUserId(long userId)
        {
            if (logger.IsVerbose) logger.Verbose("{0} UnfollowUserId({1}).", Me, userId);
            IChirperPublisher userToUnfollow = GrainFactory.GetGrain<IChirperPublisher>(userId);
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
            return WriteStateAsync();
        }

        public async Task RemoveFollower(string alias, IChirperSubscriber follower)
        {
            IEnumerable<KeyValuePair<ChirperUserInfo, IChirperSubscriber>> found = State.Followers.Where(f => f.Key.UserAlias == alias).ToList();
            if (found.Any())
            {
                ChirperUserInfo userInfo = found.FirstOrDefault().Key;
                State.Followers.Remove(userInfo);
                await WriteStateAsync();
            }
        }

        #endregion

        #region IChirperSubscriber notification callback interface

        public async Task NewChirp(ChirperMessage chirp)
        {
            if (logger.IsVerbose) logger.Verbose("{0} Received chirp message = {1}", Me, chirp);

            State.RecentReceivedMessages.Enqueue(chirp);

            // only relevant when not using fixed queue
            while (State.MyPublishedMessages.Count > PublishedMessagesCacheSize) // to keep not more than the max number of messages
            {
                State.MyPublishedMessages.Dequeue();
            }

            await WriteStateAsync();

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
        }
        #endregion


        private async Task FollowUser(long userId, string userAlias, IChirperPublisher userToFollow)
        {
            if (logger.IsVerbose) logger.Verbose("{0} FollowUser({1}).", Me, userAlias);
            await userToFollow.AddFollower(State.UserAlias, State.UserId, this);

            ChirperUserInfo userInfo = ChirperUserInfo.GetUserInfo(userId, userAlias);
            State.Subscriptions[userInfo] = userToFollow;

            await WriteStateAsync();

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

            await WriteStateAsync();

            // Notify any viewers that a subscription has been removed for this user
            viewers.Notify(
                v => v.SubscriptionRemoved(userInfo)
            );
        }
        private ChirperMessage CreateNewChirpMessage(string message)
        {
            var chirp = new ChirperMessage();
            chirp.PublisherId = State.UserId;
            chirp.PublisherAlias = State.UserAlias;
            chirp.Timestamp = DateTime.Now;
            chirp.Message = message;
            if (chirp.Message.Length > MAX_MESSAGE_LENGTH) chirp.Message = message.Substring(0, MAX_MESSAGE_LENGTH);
            return chirp;
        }

        // When reentrant grain is doing WriteStateAsync, etag violations are possible due to concurrent writes.
        // The solution is to serialize and batch writes, and make sure only a single write is outstanding at any moment in time.
        protected override async Task WriteStateAsync()
        {
            var currentWriteStateOperation = this.outstandingWriteStateOperation;
            if (currentWriteStateOperation != null)
            {
                try
                {
                    // await the outstanding write, but ignore it since it doesn't include our changes
                    await currentWriteStateOperation;
                }
                catch
                {
                    // Ignore all errors from this in-flight write operation, since the original caller(s) of it will observe it.
                }
                finally
                {
                    if (this.outstandingWriteStateOperation == currentWriteStateOperation)
                    {
                        // only null out the outstanding operation if it's the same one as the one we awaited, otherwise
                        // another request might have already done so.
                        this.outstandingWriteStateOperation = null;
                    }
                }
            }

            if (this.outstandingWriteStateOperation == null)
            {
                // If after the initial write is completed, no other request initiated a new write operation, do it now.
                currentWriteStateOperation = base.WriteStateAsync();
                this.outstandingWriteStateOperation = currentWriteStateOperation;
            }
            else
            {
                // If there were many requests enqueued to persist state, there is no reason to enqueue a new write 
                // operation for each, since any write (after the initial one that we already awaited) will have cumulative
                // changes including the one requested by our caller. Just await the new outstanding write.
                currentWriteStateOperation = this.outstandingWriteStateOperation;
            }

            try
            {
                await currentWriteStateOperation;
            }
            finally
            {
                if (this.outstandingWriteStateOperation == currentWriteStateOperation)
                {
                    // only null out the outstanding operation if it's the same one as the one we awaited, otherwise
                    // another request might have already done so.
                    this.outstandingWriteStateOperation = null;
                }
            }
        }
    }
}
