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

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace Orleans.Runtime
{
    public enum CounterStorage
    {
        DontStore,
        LogOnly,
        LogAndTable
    }

    public interface ICounter
    {
        string Name { get; }
        bool IsValueDelta { get; }
        string GetValueString();
        string GetDeltaString();
        void ResetCurrent();
        string GetDisplayString();
        CounterStorage Storage { get; }
    }

    internal interface ICounter<out T> : ICounter
    {
        T GetCurrentValue();
    }

    internal class CounterStatistic : ICounter<long>
    {
        [ThreadStatic]
        private static List<long> perOrleansThreadCounters;
        [ThreadStatic]
        private static bool isOrleansManagedThread;

        private static readonly Dictionary<string, CounterStatistic> registeredStatistics;
        private static readonly object lockable;
        private static int nextId;
        private static readonly HashSet<List<long>> allThreadCounters;
        
        private readonly int id;
        private long last;
        private bool firstStatDisplay;
        private Func<long, long> valueConverter;
        private long nonOrleansThreadsCounter; // one for all non-Orleans threads
        private readonly bool isHidden;

        public string Name { get; private set; }
        public bool UseDelta { get; private set; }
        public CounterStorage Storage { get; private set; }

        static CounterStatistic()
        {
            registeredStatistics = new Dictionary<string, CounterStatistic>();
            allThreadCounters = new HashSet<List<long>>();
            nextId = 0;
            lockable = new object();
        }

        // Must be called while Lockable is locked
        private CounterStatistic(string name, bool useDelta, CounterStorage storage, bool isHidden)
        {
            Name = name;
            UseDelta = useDelta;
            Storage = storage;
            id = Interlocked.Increment(ref nextId);
            last = 0;
            firstStatDisplay = true;
            valueConverter = null;
            nonOrleansThreadsCounter = 0;
            this.isHidden = isHidden;
        }

        internal static void SetOrleansManagedThread()
        {
            if (!isOrleansManagedThread)
            {
                lock (lockable)
                {
                    isOrleansManagedThread = true;
                    perOrleansThreadCounters = new List<long>();
                    allThreadCounters.Add(perOrleansThreadCounters);
                }
            }
        }

        public static CounterStatistic FindOrCreate(StatisticName name)
        {
            return FindOrCreate_Impl(name, true, CounterStorage.LogAndTable, false);
        }

        public static CounterStatistic FindOrCreate(StatisticName name, bool useDelta, bool isHidden = false)
        {
            return FindOrCreate_Impl(name, useDelta, CounterStorage.LogAndTable, isHidden);
        }

        public static CounterStatistic FindOrCreate(StatisticName name, CounterStorage storage, bool isHidden = false)
        {
            return FindOrCreate_Impl(name, true, storage, isHidden);
        }

        public static CounterStatistic FindOrCreate(StatisticName name, bool useDelta, CounterStorage storage, bool isHidden = false)
        {
            return FindOrCreate_Impl(name, useDelta, storage, isHidden);
        }

        private static CounterStatistic FindOrCreate_Impl(StatisticName name, bool useDelta, CounterStorage storage, bool isHidden)
        {
            lock (lockable)
            {
                CounterStatistic stat;
                if (registeredStatistics.TryGetValue(name.Name, out stat))
                {
                    return stat;
                }
                var ctr = new CounterStatistic(name.Name, useDelta, storage, isHidden);

                registeredStatistics[name.Name] = ctr;

                return ctr;
            }
        }

        static public bool Delete(string name)
        {
            lock (lockable)
            {
                return registeredStatistics.Remove(name);
            }
        }

        public CounterStatistic AddValueConverter(Func<long, long> converter)
        {
            this.valueConverter = converter;
            return this;
        }

        public void Increment()
        {
            IncrementBy(1);
        }

        public void DecrementBy(long n)
        {
            IncrementBy(-n);
        }

        // Orleans-managed threads aggregate stats in per thread local storage list.
        // For non Orleans-managed threads (.NET IO completion port threads, thread pool timer threads) we don't want to allocate a thread local storage,
        // since we don't control how many of those threads are created (could lead to too much thread local storage allocated).
        // Thus, for non Orleans-managed threads, we use a counter shared between all those threads and Interlocked.Add it (creating small contention).
        public void IncrementBy(long n)
        {
            if (isOrleansManagedThread)
            {
                while (perOrleansThreadCounters.Count <= id)
                {
                    perOrleansThreadCounters.Add(0);
                }
                perOrleansThreadCounters[id] = perOrleansThreadCounters[id] + n;
            }
            else
            {
                if (n == 1)
                {
                    Interlocked.Increment(ref nonOrleansThreadsCounter);
                }
                else
                {
                    Interlocked.Add(ref nonOrleansThreadsCounter, n);
                }
            }
        }

        /// <summary>
        /// Returns the current value
        /// </summary>
        /// <returns></returns>
        public long GetCurrentValue()
        {
            List<List<long>> lists;
            lock (lockable)
            {
                lists = allThreadCounters.ToList();
            }
            // Where(list => list.Count > id) takes only list from threads that actualy have value for this counter. 
            // The whole way we store counters is very ineffecient and better be re-written.
            long val = Interlocked.Read(ref nonOrleansThreadsCounter);
            foreach(var list in lists.Where(list => list.Count > id))
            {
                val += list[id];
            }
            return val;
            // return lists.Where(list => list.Count > id).Aggregate<List<long>, long>(0, (current, list) => current + list[id]) + nonOrleansThreadsCounter;
        }


        // does not reset delta
        public long GetCurrentValueAndDelta(out long delta)
        {
            long currentValue = GetCurrentValue();
            delta = UseDelta ? (currentValue - last) : 0;
            return currentValue;
        }

        public bool IsValueDelta { get { return UseDelta; } }

        public void ResetCurrent()
        {
            var currentValue = GetCurrentValue();
            last = currentValue;
        }

        public string GetValueString()
        {
            long current = GetCurrentValue();

            if (valueConverter != null)
            {
                try
                {
                    current = valueConverter(current);
                }
                catch (Exception) { }
            }

            return current.ToString(CultureInfo.InvariantCulture);
        }

        public string GetDeltaString()
        {
            long current = GetCurrentValue();
            long delta = UseDelta ? (current - last) : 0;

            if (valueConverter != null)
            {
                try
                {
                    delta = valueConverter(delta);
                }
                catch (Exception) { }
            }

            return delta.ToString(CultureInfo.InvariantCulture);
        }

        public string GetDisplayString()
        {
            long delta;
            
            long current = GetCurrentValueAndDelta(out delta);

            if (firstStatDisplay)
            {
                delta = 0; // Special case: don't output first delta
                firstStatDisplay = false;
            }

            if (valueConverter != null)
            {
                try
                {
                    current = valueConverter(current);
                }
                catch (Exception) { }
                try
                {
                    delta = valueConverter(delta);
                }
                catch (Exception) { }
            }

            if (delta == 0)
            {
                return String.Format("{0}.Current={1}", Name, current.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                return String.Format("{0}.Current={1},      Delta={2}", Name, current.ToString(CultureInfo.InvariantCulture), delta.ToString(CultureInfo.InvariantCulture));
            }
        }

        public override string ToString()
        {
            return GetDisplayString();
        }

        public static void AddCounters(List<ICounter> list, Func<CounterStatistic, bool> predicate)
        {
            lock (lockable)
            {
                list.AddRange(registeredStatistics.Values.Where( c => !c.isHidden && predicate(c)));
            }
        }
    }
}
