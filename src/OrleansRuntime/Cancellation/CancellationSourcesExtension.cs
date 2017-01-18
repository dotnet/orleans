using System;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Runtime.Providers;

namespace Orleans.Runtime
{
    /// <summary>
    /// Contains list of cancellation token source corresponding to the tokens
    /// passed to the related grain activation.
    /// </summary>
    internal class CancellationSourcesExtension : ICancellationSourcesExtension
    {
        private readonly static Lazy<Logger> _logger = new Lazy<Logger>(() =>
            LogManager.GetLogger(nameof(CancellationSourcesExtension), LoggerType.Runtime));

        private readonly Interner<Guid, GrainCancellationToken> _cancellationTokens;
        private static readonly TimeSpan _cleanupFrequency = TimeSpan.FromMinutes(7);
        private static readonly int _defaultInternerCollectionSize = 31;


        public CancellationSourcesExtension()
        {
            _cancellationTokens = new Interner<Guid, GrainCancellationToken>(
                 _defaultInternerCollectionSize,
                 _cleanupFrequency);
        }

        public Task CancelRemoteToken(GrainCancellationToken token)
        {
            GrainCancellationToken gct;
            if (!_cancellationTokens.TryFind(token.Id, out gct))
            {
                _logger.Value.Error(ErrorCode.CancellationTokenCancelFailed,  $"Remote token cancellation failed: token with id {token.Id} was not found");
                return TaskDone.Done;
            }

            return gct.Cancel();
        }

        /// <summary>
        /// Adds CancellationToken to the grain extension
        /// so that it can be cancelled through remote call to the CancellationSourcesExtension.
        /// </summary>
        /// <param name="target"></param>
        /// <param name="request"></param>
        /// <param name="logger"></param>
        /// <param name="siloRuntimeClient"></param>
        internal static void RegisterCancellationTokens(
            IAddressable target,
            InvokeMethodRequest request,
            Logger logger,
            ISiloRuntimeClient siloRuntimeClient)
        {
            for (var i = 0; i < request.Arguments.Length; i++)
            {
                var arg = request.Arguments[i];
                if (!(arg is GrainCancellationToken)) continue;
                var grainToken = ((GrainCancellationToken) request.Arguments[i]);

                CancellationSourcesExtension cancellationExtension;
                if (!siloRuntimeClient.TryGetExtensionHandler(out cancellationExtension))
                {
                    cancellationExtension = new CancellationSourcesExtension();
                    if (!siloRuntimeClient.TryAddExtension(cancellationExtension))
                    {
                        logger.Error(
                            ErrorCode.CancellationExtensionCreationFailed,
                            $"Could not add cancellation token extension to: {target}");
                        return;
                    }
                }

                // Replacing the half baked GrainCancellationToken that came from the wire with locally fully created one.
                request.Arguments[i] = cancellationExtension.RecordCancellationToken(grainToken.Id, grainToken.IsCancellationRequested);
            }
        }

        private GrainCancellationToken RecordCancellationToken(Guid tokenId, bool isCancellationRequested)
        {
            GrainCancellationToken localToken;
            if (_cancellationTokens.TryFind(tokenId, out localToken))
            {
                return localToken;
            }
            return _cancellationTokens.Intern(tokenId, new GrainCancellationToken(tokenId, isCancellationRequested));
        }
    }
}