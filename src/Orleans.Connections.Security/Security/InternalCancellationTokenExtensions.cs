using System;
using System.Threading;

namespace Orleans.Connections.Security.Internal
{
    internal static class InternalCancellationTokenExtensions
    {
        public static CancellationTokenRegistration UnsafeRegisterCancellation(this CancellationToken cancellationToken, Action<object> callback, object state)
        {
#if NETCOREAPP
            return cancellationToken.UnsafeRegister(callback, state);
#else
            bool restoreFlow = false;
            try
            {
                if (!ExecutionContext.IsFlowSuppressed())
                {
                    ExecutionContext.SuppressFlow();
                    restoreFlow = true;
                }

                return cancellationToken.Register(callback, state, useSynchronizationContext: false);
            }
            finally
            {
                // Restore the current ExecutionContext
                if (restoreFlow)
                {
                    ExecutionContext.RestoreFlow();
                }
            }
#endif
        }
    }
}
