using org.apache.zookeeper;
using TestExtensions;
using Xunit;

namespace Tester.ZooKeeperUtils
{
    public static class ZookeeperTestUtils
    {
        private static readonly Lazy<bool> EnsureZooKeeperLazy = new(
            () => EnsureZooKeeperAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult(),
            LazyThreadSafetyMode.PublicationOnly);

        public static void EnsureZooKeeper()
        {
            if (!EnsureZooKeeperLazy.Value)
                throw Xunit.Sdk.SkipException.ForSkip("ZooKeeper isn't running");
        }

        public static async Task<bool> EnsureZooKeeperAsync(CancellationToken cancellationToken = default)
        {
            var connectionString = TestDefaultConfiguration.ZooKeeperConnectionString;
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return false;
            }

            return await ZooKeeper
                .Using(connectionString, 2000, new ZooKeeperWatcher(), async zk =>
                {
                    try
                    {
                        await zk.existsAsync("/test", false);
                        return true;
                    }
                    catch (KeeperException.ConnectionLossException)
                    {
                        return false;
                    }
                })
                .WaitAsync(cancellationToken);
        }

        private class ZooKeeperWatcher : Watcher
        {
            public override Task process(WatchedEvent @event) => Task.CompletedTask;
        }
    }
}