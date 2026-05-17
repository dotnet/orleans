using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Runtime.Messaging;

#nullable disable
namespace Orleans.Runtime
{
    /// <summary>
    /// Monitors currently-active requests and sends status notifications to callers for long-running and blocked requests.
    /// </summary>
    internal sealed class IncomingRequestMonitor : ILifecycleParticipant<ISiloLifecycle>
    {
        private static readonly TimeSpan DefaultAnalysisPeriod = TimeSpan.FromSeconds(10);
        private readonly IAsyncTimer _scanPeriodTimer;
        private readonly ActivationWorkingSet _activationWorkingSet;
        private readonly IServiceProvider _serviceProvider;
        private readonly MessageFactory _messageFactory;
        private readonly IOptionsMonitor<SiloMessagingOptions> _messagingOptions;
        private bool _enabled = true;
        private Task _runTask;

        public IncomingRequestMonitor(
            ActivationWorkingSet activationWorkingSet,
            IAsyncTimerFactory asyncTimerFactory,
            IServiceProvider serviceProvider,
            MessageFactory messageFactory,
            IOptionsMonitor<SiloMessagingOptions> siloMessagingOptions)
        {
            _scanPeriodTimer = asyncTimerFactory.Create(TimeSpan.FromSeconds(1), nameof(IncomingRequestMonitor));
            _activationWorkingSet = activationWorkingSet;
            _serviceProvider = serviceProvider;
            _messageFactory = messageFactory;
            _messagingOptions = siloMessagingOptions;
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
                    if (_runTask is Task task)
                    {
                        await task.WaitAsync(ct).SuppressThrowing();
                    }
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

                    continue;
                }

                nextDelay = optionsPeriod;
                if (!_enabled)
                {
                    _enabled = true;
                }

                var now = DateTime.UtcNow;
                _activationWorkingSet.ForEach(
                    static (member, isIdle, state) =>
                    {
                        var (self, messageCenter, options, now) = state;
                        if (isIdle || member is not ActivationData activation)
                        {
                            return;
                        }

                        lock (activation)
                        {
                            activation.AnalyzeWorkload(now, messageCenter, self._messageFactory, options);
                        }
                    },
                    (this, messageCenter, options, now));
            }
        }
    }
}
