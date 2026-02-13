using Orleans.Concurrency;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class IdleActivationGcTestGrain1: Grain, IIdleActivationGcTestGrain1
    {
        public Task Nop()
        {
            return Task.CompletedTask;
        }
    }

    public class IdleActivationGcTestGrain2: Grain, IIdleActivationGcTestGrain2
    {
        public Task Nop()
        {
            return Task.CompletedTask;
        }
    }

    internal class BusyActivationGcTestGrain1: Grain, IBusyActivationGcTestGrain1
    {
        private readonly string _id = Guid.NewGuid().ToString();
        private readonly ActivationCollector activationCollector;
        private readonly IGrainContext _grainContext;
        private readonly TimeProvider _timeProvider;

        // Static signal shared across all activations to coordinate blocking/releasing
        private static TaskCompletionSource _globalBlockTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static readonly object _lock = new();

        /// <summary>
        /// Resets the global block signal. Call before starting blocking operations.
        /// </summary>
        public static void ResetGlobalBlock()
        {
            lock (_lock)
            {
                _globalBlockTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        /// <summary>
        /// Releases all blocked activations.
        /// </summary>
        public static void ReleaseAllBlocked()
        {
            lock (_lock)
            {
                _globalBlockTcs.TrySetResult();
            }
        }

        public BusyActivationGcTestGrain1(ActivationCollector activationCollector, IGrainContext grainContext, TimeProvider timeProvider)
        {
            this.activationCollector = activationCollector;
            _grainContext = grainContext;
            _timeProvider = timeProvider;
        }

        public Task Nop()
        {
            return Task.CompletedTask;
        }

        public Task Delay(TimeSpan dt)
        {
            // Use TimeProvider-based delay which respects FakeTimeProvider in tests
            // This allows tests to advance fake time instead of waiting real time
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var timer = _timeProvider.CreateTimer(_ => tcs.TrySetResult(), null, dt, Timeout.InfiniteTimeSpan);
            return tcs.Task.ContinueWith(_ => timer.Dispose(), TaskScheduler.Default);
        }

        public Task<string> IdentifyActivation()
        {
            return Task.FromResult(_id);
        }

        public Task BlockUntilReleased()
        {
            TaskCompletionSource tcs;
            lock (_lock)
            {
                tcs = _globalBlockTcs;
            }
            return tcs.Task;
        }
    }

    public class BusyActivationGcTestGrain2: Grain, IBusyActivationGcTestGrain2
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
        
        // Static signal shared across all activations to coordinate blocking/releasing
        private static TaskCompletionSource _globalBlockTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static readonly object _lock = new();

        /// <summary>
        /// Resets the global block signal. Call before starting blocking operations.
        /// </summary>
        public static void ResetGlobalBlock()
        {
            lock (_lock)
            {
                _globalBlockTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        /// <summary>
        /// Releases all blocked activations.
        /// </summary>
        public static void ReleaseAllBlocked()
        {
            lock (_lock)
            {
                _globalBlockTcs.TrySetResult();
            }
        }

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

        public Task BlockUntilReleased()
        {
            TaskCompletionSource tcs;
            lock (_lock)
            {
                tcs = _globalBlockTcs;
            }
            return tcs.Task;
        }

        public Task ReleaseBlock()
        {
            // This method exists for interface compatibility but actual release is done via static method
            ReleaseAllBlocked();
            return Task.CompletedTask;
        }

    }
}
