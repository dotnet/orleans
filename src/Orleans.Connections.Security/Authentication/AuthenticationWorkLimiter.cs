using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Connections.Security;

internal sealed class AuthenticationWorkLimiter
{
    private readonly QueueLimiter _inbound;
    private readonly QueueLimiter _outbound;

    public AuthenticationWorkLimiter(SiloConnectionAuthenticationOptions options)
    {
        _inbound = new(options.MaxConcurrentInboundAuthentications, options.MaxPendingInboundAuthentications);
        _outbound = new(options.MaxConcurrentOutboundAuthentications, options.MaxPendingOutboundAuthentications);
    }
    public ValueTask<IDisposable?> TryAcquireAsync(
        SiloConnectionAuthenticationDirection direction,
        CancellationToken cancellationToken) =>
        (direction == SiloConnectionAuthenticationDirection.Inbound ? _inbound : _outbound).TryAcquireAsync(cancellationToken);

    private sealed class QueueLimiter
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly int _maxPending;
        private int _pending;

        public QueueLimiter(int concurrency, int maxPending)
        {
            _semaphore = new SemaphoreSlim(concurrency, concurrency);
            _maxPending = maxPending;
        }

        public async ValueTask<IDisposable?> TryAcquireAsync(CancellationToken cancellationToken)
        {
            if (_semaphore.Wait(0, cancellationToken))
            {
                return new Releaser(_semaphore);
            }

            if (Interlocked.Increment(ref _pending) > _maxPending)
            {
                Interlocked.Decrement(ref _pending);
                return null;
            }

            try
            {
                await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new Releaser(_semaphore);
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        }
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        public Releaser(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
