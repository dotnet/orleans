using Orleans.Storage;
using System;
using System.Runtime.Serialization;

namespace Orleans.Transactions.TestKit
{
    public class RandomErrorInjector : ITransactionFaultInjector
    {
        private readonly double conflictProbability;
        private readonly double beforeProbability;
        private readonly double afterProbability;

        private readonly Random random;

        public RandomErrorInjector(double injectionProbability)
        {
            conflictProbability = injectionProbability / 5;
            beforeProbability = 2 * injectionProbability / 5;
            afterProbability = 2 * injectionProbability / 5;
            random = new Random();
        }

        public void BeforeStore()
        {
            if (random.NextDouble() < conflictProbability)
            {
                throw new RandomlyInjectedInconsistentStateException();
            }
            if (random.NextDouble() < beforeProbability)
            {
                throw new RandomlyInjectedStorageException();
            }
        }

        public void AfterStore()
        {
            if (random.NextDouble() < afterProbability)
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
