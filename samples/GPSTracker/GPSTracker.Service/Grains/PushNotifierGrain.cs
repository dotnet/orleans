using GPSTracker.Common;
using GPSTracker.GrainInterface;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GPSTracker.GrainImplementation
{
    [Reentrant]
    [StatelessWorker]
    public class PushNotifierGrain : Grain, IPushNotifierGrain
    {
        private readonly List<VelocityMessage> _messageQueue = new();
        private readonly ILogger<PushNotifierGrain> _logger;
        private List<(SiloAddress Host, IRemoteLocationHub Hub)> _hubs = new();
        public PushNotifierGrain(ILogger<PushNotifierGrain> logger) => _logger = logger;
        private Task _flushTask = Task.CompletedTask;

        public override async Task OnActivateAsync()
        {
            // Set up a timer to regularly flush the message queue
            RegisterTimer(
                _ =>
                {
                    Flush();
                    return Task.CompletedTask;
                },
                null,
                TimeSpan.FromMilliseconds(15),
                TimeSpan.FromMilliseconds(15));

            // Set up a timer to regularly refresh the hubs, to respond to azure infrastructure changes
            await RefreshHubs();
            RegisterTimer(async _ => await RefreshHubs(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));

            await base.OnActivateAsync();
        }

        public override async Task OnDeactivateAsync()
        {
            Flush();
            await _flushTask;

            await base.OnDeactivateAsync();
        }

        private async Task RefreshHubs()
        {
            // Discover the current infrastructure
            var hubListGrain = GrainFactory.GetGrain<IHubListGrain>(Guid.Empty);
            _hubs = await hubListGrain.GetHubs();
        }

        public Task SendMessage(VelocityMessage message)
        {
            // Add a message to the send queue
            _messageQueue.Add(message);
            if (_messageQueue.Count > 25)
            {
                // If the queue size is greater than 25, flush the queue
                Flush();
            }

            return Task.CompletedTask;
        }

        private void Flush()
        {
            if (_flushTask.IsCompleted)
            {
                _flushTask.Ignore();
                _flushTask = FlushInternal();
            }

            async Task FlushInternal()
            {
                if (_messageQueue.Count == 0) return;

                while (_messageQueue.Count > 25)
                {
                    // Send all messages to all SignalR hubs
                    var messagesToSend = _messageQueue.ToArray();
                    _messageQueue.Clear();

                    var tasks = new List<Task>(_hubs.Count);
                    var batch = new VelocityBatch { Messages = messagesToSend };
                    foreach (var hub in _hubs)
                    {
                        tasks.Add(BroadcastUpdates(hub.Host, hub.Hub, batch, _logger));

                        // An async local function allows for clean error logging on a per-host basis.
                        static async Task BroadcastUpdates(SiloAddress host, IRemoteLocationHub hub, VelocityBatch batch, ILogger logger)
                        {
                            try
                            {
                                await hub.BroadcastUpdates(batch);
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(ex, "Error broadcasting to host {Host}", host);
                            }
                        }
                    }

                    await Task.WhenAll(tasks);
                }
            }
        }
    }
}
