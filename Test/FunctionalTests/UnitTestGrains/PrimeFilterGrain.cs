using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Orleans;
using Orleans.Streams;
using Orleans.Runtime.Streams;

namespace UnitTestGrains
{
    /// <summary>
    /// Filters out a stream of integers to only primes.
    /// Intended as a sample - it's a horribly inefficient way to do this for real!
    /// </summary>
    public class PrimeFilterGrain : Filter
    {
        #region Overrides of SinkGrain

        protected override AsyncCompletion Process(object item)
        {
            var n = (int)item;
            if (n % 2 == 0)
                return AsyncCompletion.Done;
            var i = 3;
            while (true)
            {
                if (n != i && n % i == 0)
                    return AsyncCompletion.Done;
                if (i * i >= n)
                    return base.Process(item);
                i += 2;
            }
        }

        #endregion
    }

    /// <summary>
    /// Products a stream of integers Next, Next + Delta, ..., Max
    /// </summary>
    public class IntegerSource : Distributor
    {
        public int Next { get; set; }

        public int Delta { get; set; }

        public int Max { get; set; }

        #region Overrides of Distributor

        protected override AsyncValue<object> GetNext()
        {
            if (Next > Max)
                return null;
            var result = Next;
            Next += Delta;
            return result;
        }

        #endregion
    }
}
