using System;
using System.Runtime.Serialization;
using Orleans.Storage;

namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Randomly injects storage and state consistency failures into transactional state storage operations.
    /// </summary>
    public class RandomErrorInjector : ITransactionFaultInjector
    {
        private readonly double conflictProbability;
        private readonly double beforeProbability;
        private readonly double afterProbability;

        /// <summary>
        /// Initializes a new instance of the <see cref="RandomErrorInjector"/> class.
        /// </summary>
        /// <param name="injectionProbability">The aggregate probability of injecting a fault during a storage operation.</param>
        public RandomErrorInjector(double injectionProbability)
        {
            conflictProbability = injectionProbability / 5;
            beforeProbability = 2 * injectionProbability / 5;
            afterProbability = 2 * injectionProbability / 5;
        }

        /// <inheritdoc />
        public void BeforeStore()
        {
            if (Random.Shared.NextDouble() < conflictProbability)
            {
                throw new RandomlyInjectedInconsistentStateException();
            }
            if (Random.Shared.NextDouble() < beforeProbability)
            {
                throw new RandomlyInjectedStorageException();
            }
        }

        /// <inheritdoc />
        public void AfterStore()
        {
            if (Random.Shared.NextDouble() < afterProbability)
            {
                throw new RandomlyInjectedStorageException();
            }
        }

        /// <summary>
        /// Represents a randomly injected transactional storage failure.
        /// </summary>
        [Serializable]
        [GenerateSerializer]
        public class RandomlyInjectedStorageException : Exception
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="RandomlyInjectedStorageException"/> class.
            /// </summary>
            public RandomlyInjectedStorageException() : base("injected fault") { }

            /// <summary>
            /// Initializes a new instance of the <see cref="RandomlyInjectedStorageException"/> class from serialized data.
            /// </summary>
            /// <param name="info">The serialized exception data.</param>
            /// <param name="context">Context about the source or destination of the serialized data.</param>
            [Obsolete]
            protected RandomlyInjectedStorageException(SerializationInfo info, StreamingContext context)
                : base(info, context)
            {
            }
        }

        /// <summary>
        /// Represents a randomly injected transactional state consistency failure.
        /// </summary>
        [Serializable]
        [GenerateSerializer]
        public class RandomlyInjectedInconsistentStateException : InconsistentStateException
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="RandomlyInjectedInconsistentStateException"/> class.
            /// </summary>
            public RandomlyInjectedInconsistentStateException() : base("injected fault") { }

            /// <summary>
            /// Initializes a new instance of the <see cref="RandomlyInjectedInconsistentStateException"/> class from serialized data.
            /// </summary>
            /// <param name="info">The serialized exception data.</param>
            /// <param name="context">Context about the source or destination of the serialized data.</param>
            [Obsolete]
            protected RandomlyInjectedInconsistentStateException(SerializationInfo info, StreamingContext context)
                : base(info, context)
            {
            }
        }
    }
}
