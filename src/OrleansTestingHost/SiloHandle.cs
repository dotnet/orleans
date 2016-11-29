using System;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.TestHooks;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Represents a handle to a silo that is remotely deployed
    /// </summary>
    public abstract class SiloHandle : IDisposable
    {
        /// <summary> Get or set configuration of the silo </summary>
        public NodeConfiguration NodeConfiguration { get; set; }

        /// <summary> Get or set the name of the silo </summary>
        public string Name { get; set; }

        /// <summary>Get or set the address of the silo</summary>
        public SiloAddress SiloAddress { get; set; }

        /// <summary>Get the proxy address of the silo</summary>
        public SiloAddress ProxyAddress => SiloAddress.New(this.NodeConfiguration.ProxyGatewayEndpoint, 0);

        /// <summary>Gets whether the remote silo is expected to be active</summary>
        public abstract bool IsActive { get; }

        /// <summary>Gets or sets the silo type </summary>
        public Silo.SiloType Type { get; set; }

        /// <summary>Stop the remote silo</summary>
        /// <param name="stopGracefully">Specifies whether the silo should be stopped gracefully or abruptly.</param>
        public abstract void StopSilo(bool stopGracefully);

        /// <summary>Gets the Silo test hook</summary>
        internal ITestHooks TestHook => GrainClient.InternalGrainFactory.GetSystemTarget<ITestHooksSystemTarget>(Constants.TestHooksSystemTargetId, this.ProxyAddress);

        /// <summary>Gets the Silo test hook that uses AppDomain remoting
        /// (NOTE: this will be removed really soon, and was migrated here temporarily. It does not respect the abstraction
        /// as this only works with AppDomains for now, but we'll be removing TestHooks with AppDomains entirely)</summary>
        internal AppDomainTestHooks AppDomainTestHook { get; set; }

        /// <summary> A string that represents the current SiloHandle </summary>
        public override string ToString()
        {
            return $"(SiloHandle endpoint={SiloAddress.Endpoint} gatewayport={NodeConfiguration.ProxyGatewayEndpoint?.Port})";
        }

        /// <summary>Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.</summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.</summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!this.IsActive) return;

            // Do not attempt to perform expensive blocking operations in the finalizer thread.
            // Concrete SiloHandle implementations can do have their own cleanup functionality
            if (disposing)
            {
                StopSilo(true);
            }
        }

        /// <inheritdoc />
        ~SiloHandle()
        {
            Dispose(false);
        }
    }
}
