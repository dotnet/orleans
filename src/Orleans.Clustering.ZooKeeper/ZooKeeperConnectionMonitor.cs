using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.Membership;

internal interface IZooKeeperConnectionMonitor
{
    long CaptureAttemptGeneration();

    ValueTask<bool> WaitForConnectionAfterAsync(long connectedGeneration, CancellationToken cancellationToken);
}
