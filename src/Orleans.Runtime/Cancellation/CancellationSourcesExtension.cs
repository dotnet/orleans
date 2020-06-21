using System;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Threading;
using System.Diagnostics;

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

        public CancellationSourcesExtension(ILoggerFactory loggerFactory, IGrainCancellationTokenRuntime cancellationRuntime)
        {
            _logger = loggerFactory.CreateLogger<CancellationSourcesExtension>();
            _cancellationTokenRuntime = cancellationRuntime;
            _cleanupTimer = new Timer(obj => ((CancellationSourcesExtension)obj).ExpireTokens(), this, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            _createToken = id => new Entry(new GrainCancellationToken(id, false, _cancellationTokenRuntime));
        }

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
        /// Adds CancellationToken to the grain extension
        /// so that it can be canceled through remote call to the CancellationSourcesExtension.
        /// </summary>
        /// <param name="target"></param>
        /// <param name="request"></param>
        internal static void RegisterCancellationTokens(
            IGrainContext target,
            InvokeMethodRequest request)
        {
            for (var i = 0; i < request.Arguments.Length; i++)
            {
                var arg = request.Arguments[i];
                if (!(arg is GrainCancellationToken)) continue;
                var grainToken = ((GrainCancellationToken) request.Arguments[i]);

                var cancellationExtension = (CancellationSourcesExtension)target.GetGrainExtension<ICancellationSourcesExtension>(); 

                // Replacing the half baked GrainCancellationToken that came from the wire with locally fully created one.
                request.Arguments[i] = cancellationExtension.RecordCancellationToken(grainToken.Id, grainToken.IsCancellationRequested);
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
                var untouchedTime = TimeSpan.FromSeconds((nowTimestamp - _createdTime) / Stopwatch.Frequency);

                return untouchedTime >= expiry;
            }
        }
    }
}