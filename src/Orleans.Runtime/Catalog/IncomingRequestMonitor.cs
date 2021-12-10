using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Runtime.Messaging;

namespace Orleans.Runtime
{
    /// <summary>
    /// Monitors currently-active requests and sends status notifications to callers for long-running and blocked requests.
    /// </summary>
    internal sealed class IncomingRequestMonitor : ILifecycleParticipant<ISiloLifecycle>, IActivationWorkingSetObserver
    {
        private static readonly TimeSpan DefaultAnalysisPeriod = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan InactiveGrainIdleness = TimeSpan.FromMinutes(1);
        private readonly IAsyncTimer _scanPeriodTimer;
        private readonly IServiceProvider _serviceProvider;
        private readonly MessageFactory _messageFactory;
        private readonly IOptionsMonitor<SiloMessagingOptions> _messagingOptions;
        private readonly ConcurrentDictionary<ActivationData, bool> _recentlyUsedActivations = new ConcurrentDictionary<ActivationData, bool>(ReferenceEqualsComparer<ActivationData>.Default);
        private bool _enabled = true;
        private Task _runTask;

        public IncomingRequestMonitor(
            IAsyncTimerFactory asyncTimerFactory,
            IServiceProvider serviceProvider,
            MessageFactory messageFactory,
            IOptionsMonitor<SiloMessagingOptions> siloMessagingOptions)
        {
            _scanPeriodTimer = asyncTimerFactory.Create(TimeSpan.FromSeconds(1), nameof(IncomingRequestMonitor));
            _serviceProvider = serviceProvider;
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
            
            _recentlyUsedActivations.TryAdd(activation, true);
        }

        public void OnActive(IActivationWorkingSetMember member)
        {
            if (member is ActivationData activation)
            {
                MarkRecentlyUsed(activation);
            }
        }

        public void OnIdle(IActivationWorkingSetMember member)
        {
            if (member is ActivationData activation)
            {
                _recentlyUsedActivations.TryRemove(activation, out _);
            }
        }

        public void OnEvicted(IActivationWorkingSetMember member) => OnIdle(member);

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
            var messageCenter = _serviceProvider.GetRequiredService<MessageCenter>();

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
                    var activation = activationEntry.Key;
                    lock (activation)
                    {
                        activation.AnalyzeWorkload(now, messageCenter, _messageFactory, options);
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
