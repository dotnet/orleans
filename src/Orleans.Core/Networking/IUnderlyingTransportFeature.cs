using System.IO.Pipelines;

namespace Orleans.Runtime.Messaging
{
    /// <summary>
    /// Holds the underlying transport used by a connection.
    /// </summary>
    internal interface IUnderlyingTransportFeature
    {
        /// <summary>
        /// Gets the underlying transport.
        /// </summary>
        IDuplexPipe Transport { get; }
    }

    /// <summary>
    /// Holds the underlying transport used by a connection.
    /// </summary>
    internal class UnderlyingConnectionTransportFeature : IUnderlyingTransportFeature
    {
        /// <inheritdoc />
        public IDuplexPipe Transport { get; set; }
    }
}
