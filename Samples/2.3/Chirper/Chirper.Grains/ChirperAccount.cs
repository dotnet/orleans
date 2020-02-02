using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Chirper.Grains.Models;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;

namespace Chirper.Grains
{
    public class ChirperAccountState
    {
        /// <summary>
        /// The list of publishers who this user is following.
        /// </summary>
        public Dictionary<string, IChirperPublisher> Subscriptions { get; set; }

        /// <summary>
        /// The list of subscribers who are following this user.
        /// </summary>
        public Dictionary<string, IChirperSubscriber> Followers { get; set; }

        /// <summary>
        /// Chirp messages recently received by this user.
        /// </summary>
        public Queue<ChirperMessage> RecentReceivedMessages { get; set; }

        /// <summary>
        /// Chirp messages recently published by this user.
        /// </summary>
        public Queue<ChirperMessage> MyPublishedMessages { get; set; }
    }

    [Reentrant]
    public class ChirperAccount : Grain<ChirperAccountState>, IChirperAccount
    {
        /// <summary>
        /// Size for the recently received message cache.
        /// </summary>
        private const int ReceivedMessagesCacheSize = 100;

        /// <summary>
        /// Size for the published message cache.
        /// </summary>
        private const int PublishedMessagesCacheSize = 100;

        /// <summary>
        /// The category logger.
        /// </summary>
        private readonly ILogger<ChirperAccount> logger;

        /// <summary>
        /// Allows state writing to happen in the background.
        /// </summary>
        private Task outstandingWriteStateOperation;

        /// <summary>
        /// Max length of each chirp.
        /// </summary>
        private const int MAX_MESSAGE_LENGTH = 280;

        /// <summary>
        /// Holds the transient list of viewers.
        /// This list is not part of state and will not survive grain deactivation.
        /// </summary>
        private readonly HashSet<IChirperViewer> viewers = new HashSet<IChirperViewer>();

        public ChirperAccount(ILogger<ChirperAccount> logger)
        {
            this.logger = logger;
        }

        private string GrainType => nameof(ChirperAccount);
        private string GrainKey => this.GetPrimaryKeyString();

        #region Grain overrides

        public override Task OnActivateAsync()
        {
            // initialize state as needed
            if (this.State.RecentReceivedMessages == null) this.State.RecentReceivedMessages = new Queue<ChirperMessage>(ReceivedMessagesCacheSize);
            if (this.State.MyPublishedMessages == null) this.State.MyPublishedMessages = new Queue<ChirperMessage>(PublishedMessagesCacheSize);
            if (this.State.Followers == null) this.State.Followers = new Dictionary<string, IChirperSubscriber>();
            if (this.State.Subscriptions == null) this.State.Subscriptions = new Dictionary<string, IChirperPublisher>();

            this.logger.LogInformation("{@GrainType} {@GrainKey} activated.", this.GrainType, this.GrainKey);

            return Task.CompletedTask;
        }

        #endregion

        #region IChirperAccountGrain interface methods

        public async Task PublishMessageAsync(string message)
        {
            var chirp = this.CreateNewChirpMessage(message);

            this.logger.LogInformation("{@GrainType} {@GrainKey} publishing new chirp message '{@Chirp}'.",
                this.GrainType, this.GrainKey, chirp);

            this.State.MyPublishedMessages.Enqueue(chirp);

            while (this.State.MyPublishedMessages.Count > PublishedMessagesCacheSize)
            {
                this.State.MyPublishedMessages.Dequeue();
            }

            await this.WriteStateAsync();

            // notify viewers of new message
            this.logger.LogInformation("{@GrainType} {@GrainKey} sending new chirp message to {@ViewerCount} viewers.",
                this.GrainType, this.GrainKey, this.viewers.Count);

            this.viewers.ForEach(_ => _.NewChirp(chirp));

            // notify followers of a new message
            this.logger.LogInformation("{@GrainType} {@GrainKey} sending new chirp message to {@FollowerCount} followers.",
                this.GrainType, this.GrainKey, this.State.Followers.Count);

            await Task.WhenAll(this.State.Followers.Values.Select(_ => _.NewChirpAsync(chirp)).ToArray());
        }

        public Task<ImmutableList<ChirperMessage>> GetReceivedMessagesAsync(int n, int start)
        {
            if (start < 0) start = 0;
            if ((start + n) > this.State.RecentReceivedMessages.Count)
            {
                n = this.State.RecentReceivedMessages.Count - start;
            }

            return Task.FromResult(
                this.State.RecentReceivedMessages.Skip(start).Take(n).ToImmutableList());
        }

        public async Task FollowUserIdAsync(string username)
        {
            this.logger.LogInformation("{@GrainType} {@UserName} > FollowUserName({@TargetUserName}).",
                this.GrainType, this.GrainKey, username);

            var userToFollow = this.GrainFactory.GetGrain<IChirperPublisher>(username);

            await userToFollow.AddFollowerAsync(this.GrainKey, this.AsReference<IChirperSubscriber>());

            this.State.Subscriptions[username] = userToFollow;

            await this.WriteStateAsync();

            // notify any viewers that a subscription has been added for this user
            this.viewers.ForEach(_ => _.SubscriptionAdded(username));
        }

        public async Task UnfollowUserIdAsync(string username)
        {
            this.logger.LogInformation("{@GrainType} {@GrainKey} > UnfollowUserName({@TargetUserName}).",
                this.GrainType, this.GrainKey, username);

            // ask the publisher to remove this grain as a follower
            await GrainFactory.GetGrain<IChirperPublisher>(username)
                .RemoveFollowerAsync(this.GrainKey);

            // remove this publisher from the subscriptions list
            this.State.Subscriptions.Remove(username);

            // save now
            await this.WriteStateAsync();

            // notify event subscribers
            this.viewers.ForEach(_ => _.SubscriptionRemoved(username));
        }

        public Task<ImmutableList<string>> GetFollowingListAsync() => Task.FromResult(this.State.Subscriptions.Keys.ToImmutableList());

        public Task<ImmutableList<string>> GetFollowersListAsync() => Task.FromResult(this.State.Followers.Keys.ToImmutableList());

        public Task SubscribeAsync(IChirperViewer viewer)
        {
            this.viewers.Add(viewer);
            return Task.CompletedTask;
        }

        public Task UnsubscribeAsync(IChirperViewer viewer)
        {
            this.viewers.Remove(viewer);
            return Task.CompletedTask;
        }

        #endregion

        #region IChirperPublisher interface methods

        public Task<ImmutableList<ChirperMessage>> GetPublishedMessagesAsync(int n, int start)
        {
            if (start < 0) start = 0;
            if ((start + n) > this.State.MyPublishedMessages.Count) n = this.State.MyPublishedMessages.Count - start;
            return Task.FromResult(
                this.State.MyPublishedMessages.Skip(start).Take(n).ToImmutableList());
        }

        public async Task AddFollowerAsync(string username, IChirperSubscriber follower)
        {
            this.State.Followers[username] = follower;
            await this.WriteStateAsync();
        }

        public async Task RemoveFollowerAsync(string username)
        {
            this.State.Followers.Remove(username);
            await this.WriteStateAsync();
        }

        #endregion

        #region IChirperSubscriber notification callback interface

        public async Task NewChirpAsync(ChirperMessage chirp)
        {
            this.logger.LogInformation("{@GrainType} {@GrainKey} received chirp message = {@Chirp}",
                this.GrainType, this.GrainKey, chirp);

            this.State.RecentReceivedMessages.Enqueue(chirp);

            // only relevant when not using fixed queue
            while (this.State.MyPublishedMessages.Count > PublishedMessagesCacheSize) // to keep not more than the max number of messages
            {
                this.State.MyPublishedMessages.Dequeue();
            }

            await this.WriteStateAsync();

            // notify any viewers that a new chirp has been received
            this.logger.LogInformation("{@GrainType} {@GrainKey} sending received chirp message to {@ViewerCount} viewers",
                this.GrainType, this.GrainKey, this.viewers.Count);

            this.viewers.ForEach(_ => _.NewChirp(chirp));
        }

        #endregion

        private ChirperMessage CreateNewChirpMessage(string message) => new ChirperMessage(message, DateTime.Now, this.GrainKey);

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
