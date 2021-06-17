using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Chirper.Grains.Models;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Chirper.Grains
{
    [Serializable]
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
    public class ChirperAccount : Grain, IChirperAccount
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
        /// Max length of each chirp.
        /// </summary>
        private const int MAX_MESSAGE_LENGTH = 280;

        /// <summary>
        /// Holds the transient list of viewers.
        /// This list is not part of state and will not survive grain deactivation.
        /// </summary>
        private readonly HashSet<IChirperViewer> _viewers = new();
        private readonly ILogger<ChirperAccount> _logger;
        private readonly IPersistentState<ChirperAccountState> _state;

        /// <summary>
        /// Allows state writing to happen in the background.
        /// </summary>
        private Task _outstandingWriteStateOperation;

        public ChirperAccount(
           [PersistentState(stateName: "account", storageName: "AccountState")] IPersistentState<ChirperAccountState> state,
           ILogger<ChirperAccount> logger)
        {
            _logger = logger;
            _state = state;
        }

        private static string GrainType => nameof(ChirperAccount);
        private string GrainKey => this.GetPrimaryKeyString();

        public override Task OnActivateAsync()
        {
            // initialize state as needed
            if (_state.State.RecentReceivedMessages == null) _state.State.RecentReceivedMessages = new Queue<ChirperMessage>(ReceivedMessagesCacheSize);
            if (_state.State.MyPublishedMessages == null) _state.State.MyPublishedMessages = new Queue<ChirperMessage>(PublishedMessagesCacheSize);
            if (_state.State.Followers == null) _state.State.Followers = new Dictionary<string, IChirperSubscriber>();
            if (_state.State.Subscriptions == null) _state.State.Subscriptions = new Dictionary<string, IChirperPublisher>();

            _logger.LogInformation("{GrainType} {GrainKey} activated.", GrainType, GrainKey);

            return Task.CompletedTask;
        }

        public async Task PublishMessageAsync(string message)
        {
            var chirp = CreateNewChirpMessage(message);

            _logger.LogInformation("{GrainType} {GrainKey} publishing new chirp message '{Chirp}'.",
                GrainType, GrainKey, chirp);

            _state.State.MyPublishedMessages.Enqueue(chirp);

            while (_state.State.MyPublishedMessages.Count > PublishedMessagesCacheSize)
            {
                _state.State.MyPublishedMessages.Dequeue();
            }

            await WriteStateAsync();

            // notify viewers of new message
            _logger.LogInformation("{GrainType} {GrainKey} sending new chirp message to {ViewerCount} viewers.",
                GrainType, GrainKey, _viewers.Count);

            _viewers.ForEach(_ => _.NewChirp(chirp));

            // notify followers of a new message
            _logger.LogInformation("{GrainType} {GrainKey} sending new chirp message to {FollowerCount} followers.",
                GrainType, GrainKey, _state.State.Followers.Count);

            await Task.WhenAll(_state.State.Followers.Values.Select(_ => _.NewChirpAsync(chirp)).ToArray());
        }

        public Task<ImmutableList<ChirperMessage>> GetReceivedMessagesAsync(int n, int start)
        {
            if (start < 0) start = 0;
            if ((start + n) > _state.State.RecentReceivedMessages.Count)
            {
                n = _state.State.RecentReceivedMessages.Count - start;
            }

            return Task.FromResult(_state.State.RecentReceivedMessages.Skip(start).Take(n).ToImmutableList());
        }

        public async Task FollowUserIdAsync(string username)
        {
            _logger.LogInformation(
                "{GrainType} {UserName} > FollowUserName({TargetUserName}).",
                GrainType,
                GrainKey,
                username);

            var userToFollow = GrainFactory.GetGrain<IChirperPublisher>(username);

            await userToFollow.AddFollowerAsync(GrainKey, this.AsReference<IChirperSubscriber>());

            _state.State.Subscriptions[username] = userToFollow;

            await WriteStateAsync();

            // notify any viewers that a subscription has been added for this user
            _viewers.ForEach(_ => _.SubscriptionAdded(username));
        }

        public async Task UnfollowUserIdAsync(string username)
        {
            _logger.LogInformation(
                "{GrainType} {GrainKey} > UnfollowUserName({TargetUserName}).",
                GrainType,
                GrainKey,
                username);

            // ask the publisher to remove this grain as a follower
            await GrainFactory.GetGrain<IChirperPublisher>(username)
                .RemoveFollowerAsync(GrainKey);

            // remove this publisher from the subscriptions list
            _state.State.Subscriptions.Remove(username);

            // save now
            await WriteStateAsync();

            // notify event subscribers
            _viewers.ForEach(_ => _.SubscriptionRemoved(username));
        }

        public Task<ImmutableList<string>> GetFollowingListAsync() => Task.FromResult(_state.State.Subscriptions.Keys.ToImmutableList());

        public Task<ImmutableList<string>> GetFollowersListAsync() => Task.FromResult(_state.State.Followers.Keys.ToImmutableList());

        public Task SubscribeAsync(IChirperViewer viewer)
        {
            _viewers.Add(viewer);
            return Task.CompletedTask;
        }

        public Task UnsubscribeAsync(IChirperViewer viewer)
        {
            _viewers.Remove(viewer);
            return Task.CompletedTask;
        }

        public Task<ImmutableList<ChirperMessage>> GetPublishedMessagesAsync(int n, int start)
        {
            if (start < 0) start = 0;
            if ((start + n) > _state.State.MyPublishedMessages.Count) n = _state.State.MyPublishedMessages.Count - start;
            return Task.FromResult(_state.State.MyPublishedMessages.Skip(start).Take(n).ToImmutableList());
        }

        public async Task AddFollowerAsync(string username, IChirperSubscriber follower)
        {
            _state.State.Followers[username] = follower;
            await WriteStateAsync();
            _viewers.ForEach(_ => _.NewFollower(username));
        }

        public async Task RemoveFollowerAsync(string username)
        {
            _state.State.Followers.Remove(username);
            await WriteStateAsync();
        }

        public async Task NewChirpAsync(ChirperMessage chirp)
        {
            _logger.LogInformation(
                "{GrainType} {GrainKey} received chirp message = {Chirp}",
                GrainType,
                GrainKey,
                chirp);

            _state.State.RecentReceivedMessages.Enqueue(chirp);

            // only relevant when not using fixed queue
            while (_state.State.MyPublishedMessages.Count > PublishedMessagesCacheSize) // to keep not more than the max number of messages
            {
                _state.State.MyPublishedMessages.Dequeue();
            }

            await WriteStateAsync();

            // notify any viewers that a new chirp has been received
            _logger.LogInformation(
                "{GrainType} {GrainKey} sending received chirp message to {ViewerCount} viewers",
                GrainType,
                GrainKey,
                _viewers.Count);

            _viewers.ForEach(_ => _.NewChirp(chirp));
        }

        private ChirperMessage CreateNewChirpMessage(string message) => new ChirperMessage(message, DateTimeOffset.UtcNow, GrainKey);

        // When reentrant grain is doing WriteStateAsync, etag violations are possible due to concurrent writes.
        // The solution is to serialize and batch writes, and make sure only a single write is outstanding at any moment in time.
        private async Task WriteStateAsync()
        {
            var currentWriteStateOperation = _outstandingWriteStateOperation;
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
                    if (_outstandingWriteStateOperation == currentWriteStateOperation)
                    {
                        // only null out the outstanding operation if it's the same one as the one we awaited, otherwise
                        // another request might have already done so.
                        _outstandingWriteStateOperation = null;
                    }
                }
            }

            if (_outstandingWriteStateOperation == null)
            {
                // If after the initial write is completed, no other request initiated a new write operation, do it now.
                currentWriteStateOperation = _state.WriteStateAsync();
                _outstandingWriteStateOperation = currentWriteStateOperation;
            }
            else
            {
                // If there were many requests enqueued to persist state, there is no reason to enqueue a new write 
                // operation for each, since any write (after the initial one that we already awaited) will have cumulative
                // changes including the one requested by our caller. Just await the new outstanding write.
                currentWriteStateOperation = _outstandingWriteStateOperation;
            }

            try
            {
                await currentWriteStateOperation;
            }
            finally
            {
                if (_outstandingWriteStateOperation == currentWriteStateOperation)
                {
                    // only null out the outstanding operation if it's the same one as the one we awaited, otherwise
                    // another request might have already done so.
                    _outstandingWriteStateOperation = null;
                }
            }
        }
    }
}
