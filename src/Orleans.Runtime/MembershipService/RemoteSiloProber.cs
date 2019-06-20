using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime.MembershipService
{
    /// <inheritdoc />
    internal class RemoteSiloProber : IRemoteSiloProber
    {
        private readonly IServiceProvider serviceProvider;

        public RemoteSiloProber(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        /// <inheritdoc />
        public Task Probe(SiloAddress remoteSilo, int probeNumber)
        {
            var systemTarget = this.serviceProvider.GetRequiredService<MembershipSystemTarget>();
            return systemTarget.ProbeRemoteSilo(remoteSilo, probeNumber);
        }
    }
}
