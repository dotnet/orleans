#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime.MembershipService;

/// <inheritdoc />
internal class RemoteSiloProber(IServiceProvider serviceProvider) : IRemoteSiloProber
{
    /// <inheritdoc />
    public async Task Probe(SiloAddress remoteSilo, int probeNumber, CancellationToken cancellationToken)
    {
        var systemTarget = serviceProvider.GetRequiredService<MembershipSystemTarget>();
        await systemTarget.ProbeRemoteSilo(remoteSilo, probeNumber).WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IndirectProbeResponse> ProbeIndirectly(SiloAddress intermediary, SiloAddress target, TimeSpan probeTimeout, int probeNumber, CancellationToken cancellationToken)
    {
        var systemTarget = serviceProvider.GetRequiredService<MembershipSystemTarget>();
        return await systemTarget.ProbeRemoteSiloIndirectly(intermediary, target, probeTimeout, probeNumber).WaitAsync(cancellationToken);
    }
}
