using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans.Threading
{
    /// <summary>
    /// Rationale: on invoking the GrainReference method the CancellationTokens from method signature being wrapped in CancellationTokenWrapper.
    /// If request is local - before invoking of actual grain method it's just being unwrapped.
    /// For the remote case: on message serialization subscription on token cancel event,  
    /// which will cancel linked CancellationTokenSource located in the CancellationTokenHolderGrain, is being created, 
    /// after deserialization of the wrapper the abovementioned grain will be allocated on same silo with called grain,
    /// and wrapper will be replaced with actual token from holder grain. If CancellationTokenHolderGrain cancel 
    /// method was called before grain's allocation there is a possibility that it will be allocated on another silo than target,
    /// and it will lead to another token's roundtrip.
    /// </summary>
    internal static class CancellationTokenManager
    {
        private static TimeSpan _cancellationTokenHolderGrainDeactivationDelay;

        public static void Initialize(MessagingConfiguration config)
        {
            _cancellationTokenHolderGrainDeactivationDelay = config.CancellationTokenHolderDeactivationDelay;
        }

        /// <summary>
        /// Wraps found cancellation tokens into instances of type CancellationTokenWrapper
        /// </summary>
        /// <param name="arguments"></param>
        public static void WrapCancellationTokens(object[] arguments)
        {
            if (arguments == null) return;
            for (var i = 0; i < arguments.Length; i++)
            {
                var argument = arguments[i];
                if (argument is CancellationToken)
                {
                    arguments[i] = WrapCancellationToken((CancellationToken)argument);
                }
            }
        }

        /// <summary>
        /// Wraps cancellation token into CancellationTokenWrapper
        /// </summary>
        /// <param name="ct"> Cancellation token to be wrapped</param>
        /// <returns>CancellationTokenWrapper</returns>
        public static CancellationTokenWrapper WrapCancellationToken(CancellationToken ct)
        {
            var tokenId = Guid.NewGuid();
            return new CancellationTokenWrapper(tokenId, ct);
        }

        /// <summary>
        /// Retrieves cancellation token from holder grain
        /// </summary>
        /// <param name="tokenId"> Id of token to be retrieved</param>
        /// <returns></returns>
        public static Task<CancellationToken> GetCancellationToken(Guid tokenId)
        {
            return RuntimeClient.Current.InternalGrainFactory.GetGrain<ICancellationTokenHolderGrain>(tokenId).GetCancellationToken();
        }

        internal static void RegisterTokenCallbacks(CancellationToken ct, Guid tokenId)
        {
            if (!ct.CanBeCanceled) return;
            ct.Register((f) => Cancel(tokenId).Ignore(), new GCObserver(() => Dispose(tokenId).Ignore()));
        }

        private static async Task Cancel(Guid tokenId)
        {
            try
            {
                await RuntimeClient.Current.InternalGrainFactory.GetGrain<ICancellationTokenHolderGrain>(tokenId)
                    .Cancel(_cancellationTokenHolderGrainDeactivationDelay);
            }
            catch (Exception ex)
            {
                var logger = TraceLogger.GetLogger("CancellationTokenManager", TraceLogger.LoggerType.Runtime);
                if (logger.IsWarning)
                {
                    logger.Warn(ErrorCode.Runtime_Error_100332, "Remote token cancellation failed", ex);
                }
            }
        }

        private static async Task Dispose(Guid tokenId)
        {
            await RuntimeClient.Current.InternalGrainFactory.GetGrain<ICancellationTokenHolderGrain>(tokenId).Dispose();
        }
    }
}
