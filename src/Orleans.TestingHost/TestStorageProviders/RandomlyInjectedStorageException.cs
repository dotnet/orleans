using Orleans.Storage;
using System;
using System.Runtime.Serialization;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Represents a randomly injected storage exception.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class RandomlyInjectedStorageException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RandomlyInjectedStorageException"/> class.
        /// </summary>
        public RandomlyInjectedStorageException() : base("injected fault") { }

        /// <summary>
        /// Initializes a new instance of the <see cref="RandomlyInjectedStorageException"/> class.
        /// </summary>
        /// <param name="info">The <see cref="T:System.Runtime.Serialization.SerializationInfo" /> that holds the serialized object data about the exception being thrown.</param>
        /// <param name="context">The <see cref="T:System.Runtime.Serialization.StreamingContext" /> that contains contextual information about the source or destination.</param>
        private RandomlyInjectedStorageException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Represents a randomly injected <see cref="InconsistentStateException"/>.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class RandomlyInjectedInconsistentStateException : InconsistentStateException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RandomlyInjectedInconsistentStateException"/> class.
        /// </summary>
        public RandomlyInjectedInconsistentStateException() : base("injected fault") { }

        /// <summary>
        /// Initializes a new instance of the <see cref="RandomlyInjectedInconsistentStateException"/> class.
        /// </summary>
        /// <param name="info">The serialization info.</param>
        /// <param name="context">The context.</param>
        private RandomlyInjectedInconsistentStateException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}
