﻿using org.apache.zookeeper;
using System;
using System.Threading.Tasks;
using TestExtensions;
using Xunit;

namespace Tester.ZooKeeperUtils
{
    public static class ZookeeperTestUtils
    {
        private static readonly Lazy<bool> EnsureZooKeeperLazy = new Lazy<bool>(() => EnsureZooKeeperAsync().Result);

        public static void EnsureZooKeeper()
        {
            if (!EnsureZooKeeperLazy.Value)
                throw new SkipException("ZooKeeper isn't running");
        }

        public static async Task<bool> EnsureZooKeeperAsync()
        {
            var connectionString = TestDefaultConfiguration.ZooKeeperConnectionString;
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return false;
            }
            return await ZooKeeper.Using(connectionString, 2000, null, async zk =>
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
            });
        }
    }
}