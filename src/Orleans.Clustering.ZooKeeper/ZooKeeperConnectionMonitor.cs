using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.Membership;

internal interface IZooKeeperConnectionMonitor
{
    long CaptureAttemptGeneration();

    bool IsConnectedAfter(long connectedGeneration);

    ValueTask<bool> WaitForConnectionAfterAsync(long connectedGeneration, CancellationToken cancellationToken);

    ValueTask<IDisposable> AcquireRetryAdmissionAsync(CancellationToken cancellationToken);

    void ReportConnectionLoss(long connectedGeneration);
}
