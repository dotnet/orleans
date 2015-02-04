/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

﻿#define COLLECT_AVERAGE
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Orleans.Runtime
{
    class AverageValueStatistic
    {
#if COLLECT_AVERAGE
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1823:AvoidUnusedPrivateFields")]
        private FloatValueStatistic average;
#endif
        public string Name { get; private set; }

        static public AverageValueStatistic FindOrCreate(StatisticName name, CounterStorage storage = CounterStorage.LogOnly)
        {
            return FindOrCreate_Impl(name, storage, true);
        }

        static private AverageValueStatistic FindOrCreate_Impl(StatisticName name, CounterStorage storage, bool multiThreaded)
        {
            AverageValueStatistic stat;
#if COLLECT_AVERAGE
            if (multiThreaded)
            {
                stat = new MultiThreadedAverageValueStatistic(name);
            }
            else
            {
                stat = new SingleThreadedAverageValueStatistic(name);
            }
            stat.average = FloatValueStatistic.FindOrCreate(name,
                      () => stat.GetAverageValue(), storage);
#else
            stat = new AverageValueStatistic(name);
#endif
            
            return stat;
        }

        protected AverageValueStatistic(StatisticName name)
        {
            Name = name.Name;
        }

        public virtual void AddValue(long value) { }

        public virtual float GetAverageValue() { return 0; }

        public override string ToString()
        {
            return Name;
        }

        public AverageValueStatistic AddValueConverter(Func<float, float> converter)
        {
            this.average.AddValueConverter(converter);
            return this;
        }
    }

    internal class MultiThreadedAverageValueStatistic : AverageValueStatistic
    {
        private readonly CounterStatistic totalSum;
        private readonly CounterStatistic numItems;

        internal MultiThreadedAverageValueStatistic(StatisticName name)
            : base(name)
        {
            totalSum = CounterStatistic.FindOrCreate(new StatisticName(String.Format("{0}.{1}", name.Name, "TotalSum.Hidden")), false, CounterStorage.DontStore);
            numItems = CounterStatistic.FindOrCreate(new StatisticName(String.Format("{0}.{1}", name.Name, "NumItems.Hidden")), false, CounterStorage.DontStore);
        }

        public override void AddValue(long value)
        {
            totalSum.IncrementBy(value);
            numItems.Increment();
        }

        public override float GetAverageValue()
        {
            long nItems = this.numItems.GetCurrentValue();
            if (nItems == 0) return 0;

            long sum = this.totalSum.GetCurrentValue();
            return (float)sum / (float)nItems;
        }
    }

    // An optimized implementation to be used in a single threaded mode (not thread safe).
    internal class SingleThreadedAverageValueStatistic : AverageValueStatistic
    {
        private long totalSum;
        private long numItems;

        internal SingleThreadedAverageValueStatistic(StatisticName name)
            : base(name)
        {
            totalSum = 0;
            numItems = 0;
        }

        public override void AddValue(long value)
        {
            long oldTotal = totalSum;
            totalSum = (oldTotal + value);
            numItems = numItems + 1;
        }

        public override float GetAverageValue()
        {
            long nItems = this.numItems;
            if (nItems == 0) return 0;

            long sum = this.totalSum;
            return (float)sum / (float)nItems;
        }
    }
}