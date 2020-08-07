using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;

namespace Orleans.Runtime
{
    /// <summary>
    /// Monitors currently-active requests and sends status notifications to callers for long-running and blocked requests.
    /// </summary>
    internal sealed class IncomingRequestMonitor : ILifecycleParticipant<ISiloLifecycle>
    {
        private static readonly TimeSpan DefaultAnalysisPeriod = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan InactiveGrainIdleness = TimeSpan.FromMinutes(1);
        private readonly IAsyncTimer _scanPeriodTimer;
        private readonly IMessageCenter _messageCenter;
        private readonly MessageFactory _messageFactory;
        private readonly IOptionsMonitor<SiloMessagingOptions> _messagingOptions;
        private readonly ConcurrentDictionary<ActivationData, ActivationData> _recentlyUsedActivations = new ConcurrentDictionary<ActivationData, ActivationData>(ReferenceEqualsComparer<ActivationData>.Instance);
        private bool _enabled = true;
        private Task _runTask;

        public IncomingRequestMonitor(
            IAsyncTimerFactory asyncTimerFactory,
            IMessageCenter messageCenter,
            MessageFactory messageFactory,
            IOptionsMonitor<SiloMessagingOptions> siloMessagingOptions)
        {
            _scanPeriodTimer = asyncTimerFactory.Create(TimeSpan.FromSeconds(1), nameof(IncomingRequestMonitor));
            _messageCenter = messageCenter;
            _messageFactory = messageFactory;
            _messagingOptions = siloMessagingOptions;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MarkRecentlyUsed(ActivationData activation)
        {
            if (!_enabled)
            {
                return;
            }
            
            _recentlyUsedActivations.TryAdd(activation, activation);
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(
                nameof(IncomingRequestMonitor),
                ServiceLifecycleStage.BecomeActive,
                ct =>
                {
                    _runTask = Task.Run(this.Run);
                    return Task.CompletedTask;
                },
                async ct =>
                {
                    _scanPeriodTimer.Dispose();
                    if (_runTask is Task task) await Task.WhenAny(task, ct.WhenCancelled());
                });
        }

        private async Task Run()
        {
            var options = _messagingOptions.CurrentValue;
            var optionsPeriod = options.GrainWorkloadAnalysisPeriod;
            TimeSpan nextDelay = optionsPeriod > TimeSpan.Zero ? optionsPeriod : DefaultAnalysisPeriod;

            while (await _scanPeriodTimer.NextTick(nextDelay))
            {
                options = _messagingOptions.CurrentValue;
                optionsPeriod = options.GrainWorkloadAnalysisPeriod;

                if (optionsPeriod <= TimeSpan.Zero)
                {
                    // Scanning is disabled. Wake up and check again soon.
                    nextDelay = DefaultAnalysisPeriod;
                    if (_enabled)
                    {
                        _enabled = false;
                    }

                    _recentlyUsedActivations.Clear();
                    continue;
                }

                nextDelay = optionsPeriod;
                if (!_enabled)
                {
                    _enabled = true;
                }

                var iteration = 0;
                var now = DateTime.UtcNow;
                foreach (var activationEntry in _recentlyUsedActivations)
                {
                    var activation = activationEntry.Value;
                    lock (activation)
                    {
                        if (activation.IsInactive && activation.GetIdleness(now) > InactiveGrainIdleness)
                        {
                            _recentlyUsedActivations.TryRemove(activation, out _);
                            continue;
                        }

                        activation.AnalyzeWorkload(now, _messageCenter, _messageFactory, options);
                    }

                    // Yield execution frequently
                    if (++iteration % 100 == 0)
                    {
                        await Task.Yield();
                        now = DateTime.UtcNow;
                    }
                }
            }
        }
    }
}
