using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    internal class GrainCancellationTokenRuntime : IGrainCancellationTokenRuntime
    {
        private const int MaxNumCancelErrorTries = 3;
        private readonly TimeSpan _cancelCallMaxWaitTime = TimeSpan.FromSeconds(30);
        private readonly IBackoffProvider _cancelCallBackoffProvider = new FixedBackoff(TimeSpan.FromSeconds(1));
        private readonly Func<Exception, int, bool> _cancelCallRetryExceptionFilter =
            (exception, i) => exception is GrainExtensionNotInstalledException;

        public Task Cancel(Guid id, CancellationTokenSource tokenSource, ConcurrentDictionary<GrainId, GrainReference> grainReferences)
        {
            // propagate the exception from the _cancellationTokenSource.Cancel back to the caller
            // but also cancel _targetGrainReferences. 
            Task task = OrleansTaskExtentions.WrapInTask(tokenSource.Cancel);

            if (grainReferences.IsEmpty)
            {
                return task;
            }

            var cancellationTasks = grainReferences
                 .Select(pair => pair.Value.AsReference<ICancellationSourcesExtension>())
                 .Select(tokenExtension => CancelTokenWithRetries(id, tokenExtension))
                 .ToList();
            cancellationTasks.Add(task);

            return Task.WhenAll(cancellationTasks);
        }

        // There might be races between cancelling of the token and it's actual arriving to the target grain
        // as token on arriving causes installing of GCT extension, and without such extension the cancelling 
        // attempt will result in GrainExtensionNotInstalledException exception which shows
        // existence of race condition, so just retry in that case. 
        private Task CancelTokenWithRetries(Guid id, ICancellationSourcesExtension tokenExtension)
        {
            return AsyncExecutorWithRetries.ExecuteWithRetries(
                i => tokenExtension.CancelRemoteToken(id),
                MaxNumCancelErrorTries,
                _cancelCallRetryExceptionFilter,
                _cancelCallMaxWaitTime,
                _cancelCallBackoffProvider);
        }
    }
}
