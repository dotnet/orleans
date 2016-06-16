using System;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Runtime.Providers;

namespace Orleans.Async
{
    /// <summary>
    /// Contains list of cancellation token source corresponding to the tokens
    /// passed to the related grain activation.
    /// </summary>
    internal class CancellationSourcesExtension : ICancellationSourcesExtension
    {
        private readonly static Lazy<TraceLogger> _logger = new Lazy<TraceLogger>(() =>
            TraceLogger.GetLogger("CancellationSourcesExtension", TraceLogger.LoggerType.Runtime));
        private readonly Interner<Guid, GrainCancellationToken> _cancellationTokens;
        private static readonly TimeSpan _cleanupFrequency = TimeSpan.FromMinutes(7);
        private static readonly int _defaultInternerCollectionSize = 31;

        public CancellationSourcesExtension()
        {
            _cancellationTokens = new Interner<Guid, GrainCancellationToken>(
                _defaultInternerCollectionSize,
                _cleanupFrequency);
        }

        public Task CancelTokenSource(GrainCancellationToken token)
        {
            GrainCancellationToken gct;
            if (!_cancellationTokens.TryFind(token.Id, out gct))
            {
                _logger.Value.Error(ErrorCode.CancellationTokenCancelFailed,  $"Remote token cancellation failed: token with id {token.Id} was not found");
                return TaskDone.Done;
            }

            return gct.Cancel();
        }

        internal GrainCancellationToken GetOrCreateCancellationToken(GrainCancellationToken token)
        {
            GrainCancellationToken localToken;
            if (_cancellationTokens.TryFind(token.Id, out localToken))
            {
                if (token.IsCancellationRequested && !localToken.IsCancellationRequested)
                {
                    localToken.Cancel();
                }

                return localToken;
            }

            return _cancellationTokens.Intern(token.Id, new GrainCancellationToken(token.Id, token.IsCancellationRequested));
        }

        /// <summary>
        /// Adds CancellationToken to the grain extension
        /// so that it can be cancelled through remote call to the CancellationSourcesExtension.
        /// </summary>
        /// <param name="target"></param>
        /// <param name="request"></param>
        /// <param name="i">Index of the GrainCancellationToken in the request.Arguments array</param>
        /// <param name="logger"></param>
        internal static void RegisterCancellationTokens(IAddressable target, InvokeMethodRequest request, TraceLogger logger)
        {
            for (var i = 0; i < request.Arguments.Length; i++)
            {
                var arg = request.Arguments[i];
                if (!(arg is GrainCancellationToken)) continue;
                var grainToken = ((GrainCancellationToken) request.Arguments[i]);

                CancellationSourcesExtension cancellationExtension;
                if (!SiloProviderRuntime.Instance.TryGetExtensionHandler(out cancellationExtension))
                {
                    cancellationExtension = new CancellationSourcesExtension();
                    if (!SiloProviderRuntime.Instance.TryAddExtension(cancellationExtension))
                    {
                        logger.Error(
                            ErrorCode.CancellationExtensionCreationFailed,
                            string.Format("Could not add cancellation token extension, target: {0}", target));
                        return;
                    }
                }

                // Replacing the GrainCancellationToken that came from the wire with locally created one.
                request.Arguments[i] = cancellationExtension.GetOrCreateCancellationToken(grainToken);
            }
        }
    }
}