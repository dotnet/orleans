using System.Threading.Tasks;
using org.apache.zookeeper;
using Microsoft.Extensions.Logging;
using static org.apache.zookeeper.Watcher.Event;

namespace Orleans.Runtime.Membership;

/// <summary>
/// the state of every ZooKeeper client and its push notifications are published using watchers.
/// in orleans the watcher is only for debugging purposes
/// </summary>
internal class ZooKeeperWatcher : Watcher
{
    private readonly ILogger logger;
    private TaskCompletionSource<KeeperState> tcs = new();
    public Task WaitForConnectionAsync() => tcs.Task;

    public ZooKeeperWatcher(ILogger logger)
    {
        this.logger = logger;
    }

    public override Task process(WatchedEvent @event)
    {
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.Debug(@event.ToString());
        }
        
        if (@event.getState() is KeeperState.Disconnected)
        {
            logger.LogError("ZooKeeper disconnected", @event);

            // if the task is already completed, create a new one
            if (tcs.Task.IsCompleted)
                tcs = new();
        }
        else if (@event.getState() is KeeperState.SyncConnected)
        {
            tcs.TrySetResult(KeeperState.SyncConnected);
        }

        return Task.CompletedTask;
    }
}