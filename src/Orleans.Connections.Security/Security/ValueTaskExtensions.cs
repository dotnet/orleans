using System;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Orleans.Connections.Security
{
    internal static class ValueTaskExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Task GetAsTask(this in ValueTask<FlushResult> valueTask)
        {
            // Try to avoid the allocation from AsTask
            if (valueTask.IsCompletedSuccessfully)
            {
                // Signal consumption to the IValueTaskSource
                var flushResult = valueTask.GetAwaiter().GetResult();
                if (flushResult.IsCanceled) throw new OperationCanceledException();
                return Task.CompletedTask;
            }
            else
            {
                return AwaitFlush(valueTask);
                static async Task AwaitFlush(ValueTask<FlushResult> valueTask)
                {
                    var result = await valueTask.ConfigureAwait(false);
                    if (result.IsCanceled) throw new OperationCanceledException();
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ValueTask GetAsValueTask(this in ValueTask<FlushResult> valueTask)
        {
            // Try to avoid the allocation from AsTask
            if (valueTask.IsCompletedSuccessfully)
            {
                // Signal consumption to the IValueTaskSource
                var flushResult = valueTask.GetAwaiter().GetResult();
                if (flushResult.IsCanceled) throw new OperationCanceledException();
                return default;
            }
            else
            {
                return AwaitFlush(valueTask);
                static async ValueTask AwaitFlush(ValueTask<FlushResult> valueTask)
                {
                    var result = await valueTask.ConfigureAwait(false);
                    if (result.IsCanceled) throw new OperationCanceledException();
                }
            }
        }
    }
}
