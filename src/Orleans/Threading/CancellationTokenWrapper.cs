using System;
using System.Threading;

namespace Orleans.Threading
{
    /// <summary>
    /// Used as replacement of CancellationToken during network roundtrips
    /// </summary>
    [Serializable]
    internal class CancellationTokenWrapper
    {
        public CancellationTokenWrapper(Guid id, CancellationToken cancellationToken)
            : this(id)
        {
            CancellationToken = cancellationToken;
        }
        public CancellationTokenWrapper(Guid id)
            : this()
        {
            Id = id;
        }

        public CancellationTokenWrapper()
        {
            WentThroughSerialization = false;
        }

        /// <summary>
        /// Unique id of concrete token
        /// </summary>
        public Guid Id { get; private set; }

        /// <summary>
        /// Cancellation token
        /// </summary>
        public CancellationToken CancellationToken { get; private set; }

        /// <summary>
        /// Shows whether wrapper went though serialization process
        /// </summary>
        public bool WentThroughSerialization { get; set; }
    }
}
