using Orleans.Concurrency;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class IdleActivationGcTestGrain1 : Grain, IIdleActivationGcTestGrain1
    {
        public Task Nop()
        {
            return Task.CompletedTask;
        }
    }

    public class IdleActivationGcTestGrain2 : Grain, IIdleActivationGcTestGrain2
    {
        public Task Nop()
        {
            return Task.CompletedTask;
        }
    }

    internal class BusyActivationGcTestGrain1 : Grain, IBusyActivationGcTestGrain1
    {
        private static BlockingCallBarrier? _blockingCallBarrier;
        private readonly string _id = Guid.NewGuid().ToString();
        private readonly ActivationCollector activationCollector;
        private readonly IGrainContext _grainContext;

        public BusyActivationGcTestGrain1(ActivationCollector activationCollector, IGrainContext grainContext)
        {
            this.activationCollector = activationCollector;
            _grainContext = grainContext;
        }

        public Task Nop()
        {
            return Task.CompletedTask;
        }

        public Task BlockUntilReleased()
        {
            var barrier = Volatile.Read(ref _blockingCallBarrier)
                ?? throw new InvalidOperationException("The blocking call barrier is not armed.");
            return barrier.SignalAndWaitAsync();
        }

        public Task Delay(TimeSpan dt)
        {
            return Task.Delay(dt);
        }

        public Task<string> IdentifyActivation()
        {
            return Task.FromResult(_id);
        }

        public static BlockingCallBarrier ArmBlockingCallBarrier(int participantCount)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(participantCount, 1);

            var barrier = new BlockingCallBarrier(participantCount);
            if (Interlocked.CompareExchange(ref _blockingCallBarrier, barrier, null) is not null)
            {
                throw new InvalidOperationException("A blocking call barrier is already armed.");
            }

            return barrier;
        }

        public sealed class BlockingCallBarrier : IDisposable
        {
            private readonly TaskCompletionSource _participantsReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly int _participantCount;
            private int _remainingParticipants;
            private int _disposed;

            internal BlockingCallBarrier(int participantCount)
            {
                _participantCount = participantCount;
                _remainingParticipants = participantCount;
            }

            public async Task WaitForParticipantsAsync(TimeSpan timeout, CancellationToken cancellationToken)
            {
                try
                {
                    await _participantsReady.Task.WaitAsync(timeout, cancellationToken);
                }
                catch (TimeoutException exception)
                {
                    var arrived = _participantCount - Volatile.Read(ref _remainingParticipants);
                    throw new TimeoutException(
                        $"Timed out waiting for busy activation calls: {arrived} of {_participantCount} calls reached the barrier.",
                        exception);
                }
            }

            public void Release() => _release.TrySetResult();

            internal async Task SignalAndWaitAsync()
            {
                var remaining = Interlocked.Decrement(ref _remainingParticipants);
                if (remaining < 0)
                {
                    throw new InvalidOperationException($"More than {_participantCount} calls reached the blocking call barrier.");
                }

                if (remaining == 0)
                {
                    _participantsReady.TrySetResult();
                }

                await _release.Task;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                Release();
                Interlocked.CompareExchange(ref _blockingCallBarrier, null, this);
            }
        }
    }

    public class BusyActivationGcTestGrain2 : Grain, IBusyActivationGcTestGrain2
    {
        public Task Nop()
        {
            return Task.CompletedTask;
        }
    }

    public class CollectionSpecificAgeLimitForTenSecondsActivationGcTestGrain : Grain, ICollectionSpecificAgeLimitForTenSecondsActivationGcTestGrain
    {
        public Task Nop()
        {
            return Task.CompletedTask;
        }
    }

    // Use this Test Class in Non.Silo test [SiloBuilder_GrainCollectionOptionsForZeroSecondsAgeLimitTest]
    public class CollectionSpecificAgeLimitForZeroSecondsActivationGcTestGrain : Grain, ICollectionSpecificAgeLimitForZeroSecondsActivationGcTestGrain
    {
        public Task Nop()
        {
            return Task.CompletedTask;
        }
    }

    [StatelessWorker]
    public class StatelessWorkerActivationCollectorTestGrain1 : Grain, IStatelessWorkerActivationCollectorTestGrain1
    {
        private readonly string _id = Guid.NewGuid().ToString();

        public Task Nop()
        {
            return Task.CompletedTask;
        }

        public Task Delay(TimeSpan dt)
        {
            return Task.Delay(dt);
        }

        public Task<string> IdentifyActivation()
        {
            return Task.FromResult(_id);
        }

    }

    public class KeepAliveActivationGcTestGrain : Grain, IKeepAliveActivationGcTestGrain
    {
        public Task SetKeepAlive(TimeSpan keepAlive)
        {
            DelayDeactivation(keepAlive);
            return Task.CompletedTask;
        }

        public Task CancelKeepAlive()
        {
            DelayDeactivation(TimeSpan.Zero);
            return Task.CompletedTask;
        }
    }
}
