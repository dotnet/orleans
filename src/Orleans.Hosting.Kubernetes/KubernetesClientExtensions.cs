using k8s;
using k8s.Autorest;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Orleans.Hosting.Kubernetes
{
    internal static class KubernetesClientExtensions
    {
        public static async IAsyncEnumerable<(WatchEventType EventType, TValue Value)> WatchAsync<TList, TValue>(this HttpOperationResponse<TList> watchList, [EnumeratorCancellation] CancellationToken cancellation)
        {
            Channel<(WatchEventType, TValue)> channel = Channel.CreateUnbounded<(WatchEventType, TValue)>(
                new UnboundedChannelOptions
                {
                    AllowSynchronousContinuations = false,
                    SingleReader = true,
                    SingleWriter = true
                });

            var reader = channel.Reader;
            Watcher<TValue>[] watcher = new Watcher<TValue>[] { default };
            var cancellationRegistration = cancellation.Register(() =>
            {
                _ = channel.Writer.TryComplete();
                watcher[0]?.Dispose();
            });

            watcher[0] = watchList.Watch<TValue, TList>((eventType, value) =>
            {
                _ = channel.Writer.TryWrite((eventType, value));
            },
            exception =>
            {
                _ = channel.Writer.TryComplete(exception);
                cancellationRegistration.Dispose();
            },
            () =>
            {
                _ = channel.Writer.TryComplete();
                cancellationRegistration.Dispose();
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    await channel.Reader.Completion.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // suppress exception set by channel writer and re-thrown by Completion task
                }
                finally
                {
                    watcher[0].Dispose();
                    cancellationRegistration.Dispose();
                }
            });


            while (await channel.Reader.WaitToReadAsync(cancellation))
            {
                while (reader.TryRead(out var item))
                {
                    yield return item;
                }
            }
        }
    }
}
