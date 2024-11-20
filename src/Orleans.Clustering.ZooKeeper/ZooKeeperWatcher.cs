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
    // we don't really need the type, however netstandard2.0 doesn't have non generic TaskCompletionSource
    private TaskCompletionSource<KeeperState> tcs = new();

    public Task WaitForConnectionAsync() => tcs.Task;

    public ZooKeeperWatcher(ILogger logger)
    {
        this.logger = logger;
    }

    public override Task process(WatchedEvent ev)
    {
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.Debug(ev.ToString());
        }

        switch (ev.getState())
        {
            case KeeperState.AuthFailed:
                logger.LogError("ZooKeeper authentication failed", ev);
                ResetTaskCompletionSource(ref tcs);
                break;
            case KeeperState.Expired:
                logger.LogError("ZooKeeper session expired", ev);
                ResetTaskCompletionSource(ref tcs);
                break;
            case KeeperState.Disconnected:
                logger.LogError("ZooKeeper disconnected", ev);
                ResetTaskCompletionSource(ref tcs);
                break;
            case KeeperState.SyncConnected:
                logger.LogInformation("ZooKeeper connected", ev);
                tcs.TrySetResult(KeeperState.SyncConnected);
                break;
            case KeeperState.ConnectedReadOnly:
                logger.LogInformation("ZooKeeper connected", ev);
                tcs.TrySetResult(KeeperState.ConnectedReadOnly);
                break;
        }

        return Task.CompletedTask;

        static void ResetTaskCompletionSource(ref TaskCompletionSource<KeeperState> tcs)
        {
            if (tcs.Task.IsCompleted)
            {
                tcs = new();
            }
        }
    }
}