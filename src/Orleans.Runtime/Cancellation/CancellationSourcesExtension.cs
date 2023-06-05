using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Threading;
using System.Diagnostics;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime
{
    /// <summary>
    /// Contains list of cancellation token source corresponding to the tokens
    /// passed to the related grain activation.
    /// </summary>
    internal class CancellationSourcesExtension : ICancellationSourcesExtension, IDisposable
    {
        private readonly ConcurrentDictionary<Guid, Entry> _cancellationTokens = new ConcurrentDictionary<Guid, Entry>();
        private readonly ILogger _logger;
        private readonly IGrainCancellationTokenRuntime _cancellationTokenRuntime;
        private readonly Timer _cleanupTimer;
        private readonly Func<Guid, Entry> _createToken;
        private static readonly TimeSpan _cleanupFrequency = TimeSpan.FromMinutes(7);

        /// <summary>
        /// Initializes a new instance of the <see cref="CancellationSourcesExtension"/> class.
        /// </summary>
        /// <param name="loggerFactory">The logger factory.</param>
        /// <param name="cancellationRuntime">The cancellation runtime.</param>
        public CancellationSourcesExtension(ILoggerFactory loggerFactory, IGrainCancellationTokenRuntime cancellationRuntime)
        {
            _logger = loggerFactory.CreateLogger<CancellationSourcesExtension>();
            _cancellationTokenRuntime = cancellationRuntime;
            _cleanupTimer = new Timer(obj => ((CancellationSourcesExtension)obj).ExpireTokens(), this, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            _createToken = id => new Entry(new GrainCancellationToken(id, false, _cancellationTokenRuntime));
        }

        /// <inheritdoc />
        public Task CancelRemoteToken(Guid tokenId)
        {
            if (!_cancellationTokens.TryGetValue(tokenId, out var entry))
            {
                _logger.LogWarning((int)ErrorCode.CancellationTokenCancelFailed, "Received a cancel call for token with id {TokenId}, but the token was not found", tokenId);

                // Record the cancellation anyway, in case the call which would have registered the cancellation is still pending.
                this.RecordCancellationToken(tokenId, isCancellationRequested: true);
                return Task.CompletedTask;
            }

            entry.Touch();
            var token = entry.Token;
            return token.Cancel();
        }

        /// <summary>
        /// Adds <see cref="CancellationToken"/> to the grain extension so that it can be canceled through remote call to the CancellationSourcesExtension.
        /// </summary>
        /// <param name="target"></param>
        /// <param name="request"></param>
        internal static void RegisterCancellationTokens(
            IGrainContext target,
            IInvokable request)
        {
            var argumentCount = request.GetArgumentCount();
            for (var i = 0; i < argumentCount; i++)
            {
                var arg = request.GetArgument(i);
                if (arg is not GrainCancellationToken grainToken)
                {
                    continue;
                }

                var cancellationExtension = (CancellationSourcesExtension)target.GetGrainExtension<ICancellationSourcesExtension>();

                // Replacing the half baked GrainCancellationToken that came from the wire with locally fully created one.
                request.SetArgument(i, cancellationExtension.RecordCancellationToken(grainToken.Id, grainToken.IsCancellationRequested));
            }
        }

        private GrainCancellationToken RecordCancellationToken(Guid tokenId, bool isCancellationRequested)
        {
            if (_cancellationTokens.TryGetValue(tokenId, out var entry))
            {
                entry.Touch();
                return entry.Token;
            }

            entry = _cancellationTokens.GetOrAdd(tokenId, _createToken);
            if (isCancellationRequested)
            {
                entry.Token.Cancel();
            }

            return entry.Token;
        }

        private void ExpireTokens()
        {
            var now = Stopwatch.GetTimestamp();
            foreach (var token in _cancellationTokens)
            {
                if (token.Value.IsExpired(_cleanupFrequency, now))
                {
                    _cancellationTokens.TryRemove(token.Key, out _);
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _cleanupTimer.Dispose();
        }

        private class Entry
        {
            private long _createdTime;

            public Entry(GrainCancellationToken token)
            {
                Token = token;
                _createdTime = Stopwatch.GetTimestamp();
            }

            public void Touch() => _createdTime = Stopwatch.GetTimestamp();

            public GrainCancellationToken Token { get; }

            public bool IsExpired(TimeSpan expiry, long nowTimestamp)
            {
                var untouchedTime = TimeSpan.FromSeconds((nowTimestamp - _createdTime) / (double)Stopwatch.Frequency);

                return untouchedTime >= expiry;
            }
        }
    }
}