using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime;

internal sealed class RejectingInterClusterRequestAuthorizer : IInterClusterRequestAuthorizer
{
    public ValueTask Authorize(
        ClusterIdentity source,
        UniversalReference target,
        CancellationToken cancellationToken = default)
        => ValueTask.FromException(
            new UnauthorizedAccessException(
                $"Inter-cluster requests from '{source}' require an explicit {nameof(IInterClusterRequestAuthorizer)} registration."));
}
