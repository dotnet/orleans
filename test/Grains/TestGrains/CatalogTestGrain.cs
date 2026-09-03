using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class CatalogTestGrain : Grain, ICatalogTestGrain
    {
        private static ConcurrentCallBarrier? _concurrentCallBarrier;
        private readonly string _activationId = Guid.NewGuid().ToString();

        public static ConcurrentCallBarrier ArmConcurrentCallBarrier(int participantCount)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(participantCount, 1);

            var barrier = new ConcurrentCallBarrier(participantCount);
            if (Interlocked.CompareExchange(ref _concurrentCallBarrier, barrier, null) is not null)
            {
                throw new InvalidOperationException("A concurrent call barrier is already armed.");
            }

            return barrier;
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            return Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }

        public Task Initialize()
        {
            return Task.CompletedTask;
        }

        public Task<string> GetActivationId()
        {
            return Task.FromResult(_activationId);
        }

        public async Task<string[]> GetActivationIds(int nGrains, long startingKey)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(nGrains);

            if (Volatile.Read(ref _concurrentCallBarrier) is { } barrier)
            {
                await barrier.SignalAndWaitAsync();
            }

            var promises = new Task<string>[nGrains];
            for (int i = 0; i < nGrains; i++)
            {
                var grain = GrainFactory.GetGrain<ICatalogTestGrain>(startingKey + i);
                promises[i] = grain.GetActivationId();
            }

            return await Task.WhenAll(promises);
        }

        public sealed class ConcurrentCallBarrier : IDisposable
        {
            private readonly TaskCompletionSource _participantsReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly int _participantCount;
            private int _remainingParticipants;
            private int _disposed;

            internal ConcurrentCallBarrier(int participantCount)
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
                        $"Timed out waiting for concurrent catalog calls: {arrived} of {_participantCount} runners reached the barrier.",
                        exception);
                }
            }

            public void Release()
            {
                _release.TrySetResult();
            }

            internal async Task SignalAndWaitAsync()
            {
                var remaining = Interlocked.Decrement(ref _remainingParticipants);
                if (remaining < 0)
                {
                    throw new InvalidOperationException($"More than {_participantCount} runners reached the concurrent call barrier.");
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
                Interlocked.CompareExchange(ref _concurrentCallBarrier, null, this);
            }
        }
    }
}
