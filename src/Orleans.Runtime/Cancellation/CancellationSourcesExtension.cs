using System;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Runtime.Providers;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    /// <summary>
    /// Contains list of cancellation token source corresponding to the tokens
    /// passed to the related grain activation.
    /// </summary>
    internal class CancellationSourcesExtension : ICancellationSourcesExtension
    {
        private readonly ILogger _logger;

        private readonly Interner<Guid, GrainCancellationToken> _cancellationTokens;
        private static readonly TimeSpan _cleanupFrequency = TimeSpan.FromMinutes(7);
        private const int _defaultInternerCollectionSize = 31;


        public CancellationSourcesExtension(ILoggerFactory loggerFactory)
        {
            _cancellationTokens = new Interner<Guid, GrainCancellationToken>(
                 _defaultInternerCollectionSize,
                 _cleanupFrequency);
            _logger = loggerFactory.CreateLogger<CancellationSourcesExtension>();
        }

        public Task CancelRemoteToken(Guid tokenId)
        {
            GrainCancellationToken gct;
            if (!_cancellationTokens.TryFind(tokenId, out gct))
            {
                _logger.Error(ErrorCode.CancellationTokenCancelFailed,  $"Remote token cancellation failed: token with id {tokenId} was not found");
                return Task.CompletedTask;
            }

            return gct.Cancel();
        }

        /// <summary>
        /// Adds CancellationToken to the grain extension
        /// so that it can be cancelled through remote call to the CancellationSourcesExtension.
        /// </summary>
        /// <param name="target"></param>
        /// <param name="request"></param>
        /// <param name="loggerFactory">logger factory configured in current cluster</param>
        /// <param name="logger">caller's logger</param>
        /// <param name="siloRuntimeClient"></param>
        /// <param name="cancellationTokenRuntime"></param>
        internal static void RegisterCancellationTokens(
            IAddressable target,
            InvokeMethodRequest request,
            ILoggerFactory loggerFactory,
            ILogger logger,
            ISiloRuntimeClient siloRuntimeClient,
            IGrainCancellationTokenRuntime cancellationTokenRuntime)
        {
            for (var i = 0; i < request.Arguments.Length; i++)
            {
                var arg = request.Arguments[i];
                if (!(arg is GrainCancellationToken)) continue;
                var grainToken = ((GrainCancellationToken) request.Arguments[i]);

                CancellationSourcesExtension cancellationExtension;
                if (!siloRuntimeClient.TryGetExtensionHandler(out cancellationExtension))
                {
                    cancellationExtension = new CancellationSourcesExtension(loggerFactory);
                    if (!siloRuntimeClient.TryAddExtension(cancellationExtension))
                    {
                        logger.Error(
                            ErrorCode.CancellationExtensionCreationFailed,
                            $"Could not add cancellation token extension to: {target}");
                        return;
                    }
                }

                // Replacing the half baked GrainCancellationToken that came from the wire with locally fully created one.
                request.Arguments[i] = cancellationExtension.RecordCancellationToken(grainToken.Id, grainToken.IsCancellationRequested, cancellationTokenRuntime);
            }
        }

        private GrainCancellationToken RecordCancellationToken(Guid tokenId, bool isCancellationRequested, IGrainCancellationTokenRuntime cancellationTokenRuntime)
        {
            GrainCancellationToken localToken;
            if (_cancellationTokens.TryFind(tokenId, out localToken))
            {
                return localToken;
            }
            return _cancellationTokens.Intern(tokenId, new GrainCancellationToken(tokenId, isCancellationRequested, cancellationTokenRuntime));
        }
    }
}