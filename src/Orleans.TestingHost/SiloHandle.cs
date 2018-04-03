using System;
using Orleans.Runtime;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Represents a handle to a silo that is remotely deployed
    /// </summary>
    public abstract class SiloHandle : IDisposable
    {
        /// <summary> Get or set configuration of the cluster </summary>
        public TestClusterOptions ClusterOptions { get; set; }

        /// <summary> Gets or sets the instance number within the cluster.</summary>
        public short InstanceNumber { get; set; }

        /// <summary> Get or set the name of the silo </summary>
        public string Name { get; set; }

        /// <summary>Get or set the address of the silo</summary>
        public SiloAddress SiloAddress { get; set; }

        ///// <summary>Get the proxy address of the silo</summary>
        public SiloAddress GatewayAddress { get; set; }

        /// <summary>Gets whether the remote silo is expected to be active</summary>
        public abstract bool IsActive { get; }

        /// <summary>Stop the remote silo</summary>
        /// <param name="stopGracefully">Specifies whether the silo should be stopped gracefully or abruptly.</param>
        public abstract void StopSilo(bool stopGracefully);

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