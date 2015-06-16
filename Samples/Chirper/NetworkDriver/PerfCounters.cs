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
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Configuration.Install;
using System.ComponentModel;
using Orleans;

namespace Orleans.Samples.Chirper.Network.Driver
{
    internal interface IChirperPerformanceCounterLong
    {
        void Decrement();
        void Increment();
        void IncrementBy(long value);
        long RawValue { get; set; }
    }

    internal interface IDriverPerformanceCounters
    {
        IChirperPerformanceCounterLong ChirpsPerSecond { get; }
    }

    internal class ChirperPerformanceCounters : IDriverPerformanceCounters
    {
        internal const string CategoryName = "ChirperDriver";
        internal const string ChirpsPerSecondName = "ChirpsPerSecond";

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "Instances of PerformanceCounter outlive this method.")]
        public ChirperPerformanceCounters(string instanceName)
        {

            ChirpsPerSecond = new ChirperPerformanceCounterLong(null);  // in case we fail to open the counters, initialize with dummies doing nothing so that the perf counter calls are ignored
            
            if (PerformanceCounterCategory.Exists(CategoryName))
            {
                try
                {
                    ChirpsPerSecond = new ChirperPerformanceCounterLong(new PerformanceCounter(CategoryName, ChirpsPerSecondName, instanceName, false));
                }
                catch
                {
                    Console.WriteLine("Failed to initialize performance counters");
                }
            }
            else
            {
                Console.WriteLine("Performance counter category {0} not found. Make sure the category and the counters are registered properly.", CategoryName);
            }
        }

        public IChirperPerformanceCounterLong ChirpsPerSecond { get; private set; }

        public void ResetAll()
        {
            ChirpsPerSecond.RawValue = 0;
        }

        class ChirperPerformanceCounterLong : IChirperPerformanceCounterLong
        {
            readonly PerformanceCounter counter;

            public ChirperPerformanceCounterLong(PerformanceCounter counter)
            {
                this.counter = counter;
            }

            public void Decrement()
            {
                if (counter != null)
                    counter.Decrement();
            }

            public void Increment()
            {
                if (counter != null)
                    counter.Increment();
            }

            public void IncrementBy(long value)
            {
                if (counter != null)
                    counter.IncrementBy(value);
            }

            public long RawValue
            {
                get
                {
                    return (counter != null) ? counter.RawValue : 0;
                }
                set
                {
                    if (counter != null)
                        counter.RawValue = value;
                }
            }
        }
    }


    [RunInstaller(true)]
    public class OrleansPerformanceCounterInstaller : Installer
    {
        public OrleansPerformanceCounterInstaller()
        {
            try
            {
                using (PerformanceCounterInstaller myPerformanceCounterInstaller = new PerformanceCounterInstaller())
                {
                    myPerformanceCounterInstaller.CategoryName = ChirperPerformanceCounters.CategoryName;
                    myPerformanceCounterInstaller.CategoryType = PerformanceCounterCategoryType.MultiInstance;
                    myPerformanceCounterInstaller.Counters.Add(new CounterCreationData(ChirperPerformanceCounters.ChirpsPerSecondName, "Number of grains", PerformanceCounterType.NumberOfItems32));
                    Installers.Add(myPerformanceCounterInstaller);
                }
            }
            catch (Exception exc)
            {
                this.Context.LogMessage("Failed to install performance counters: " + exc.Message);
            }
        }
    }
}
