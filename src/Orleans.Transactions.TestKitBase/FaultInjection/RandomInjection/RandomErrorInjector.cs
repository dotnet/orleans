using System;
using System.Runtime.Serialization;
using Orleans.Internal;
using Orleans.Storage;

namespace Orleans.Transactions.TestKit
{
    public class RandomErrorInjector : ITransactionFaultInjector
    {
        private readonly double conflictProbability;
        private readonly double beforeProbability;
        private readonly double afterProbability;

        public RandomErrorInjector(double injectionProbability)
        {
            conflictProbability = injectionProbability / 5;
            beforeProbability = 2 * injectionProbability / 5;
            afterProbability = 2 * injectionProbability / 5;
        }

        public void BeforeStore()
        {
            if (ThreadSafeRandom.NextDouble() < conflictProbability)
            {
                throw new RandomlyInjectedInconsistentStateException();
            }
            if (ThreadSafeRandom.NextDouble() < beforeProbability)
            {
                throw new RandomlyInjectedStorageException();
            }
        }

        public void AfterStore()
        {
            if (ThreadSafeRandom.NextDouble() < afterProbability)
            {
                throw new RandomlyInjectedStorageException();
            }
        }

        [Serializable]
        public class RandomlyInjectedStorageException : Exception
        {
            public RandomlyInjectedStorageException() : base("injected fault") { }

            protected RandomlyInjectedStorageException(SerializationInfo info, StreamingContext context)
                : base(info, context)
            {
            }
        }

        [Serializable]
        public class RandomlyInjectedInconsistentStateException : InconsistentStateException
        {
            public RandomlyInjectedInconsistentStateException() : base("injected fault") { }

            protected RandomlyInjectedInconsistentStateException(SerializationInfo info, StreamingContext context)
                : base(info, context)
            {
            }
        }
    }
}
