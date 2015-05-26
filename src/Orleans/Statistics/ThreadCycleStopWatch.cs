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
using System.Runtime.InteropServices;

namespace Orleans.Runtime
{
    /// <summary>
    /// Stopwatch for CPU time of a thread. 
    /// You must only use Start, Stop, and Restart from thread being measured!
    /// CANNOT call this class from a different thread that is not the currently executing thread. 
    /// Otherwise, QueryThreadCycleTime returns undefined (garbage) results. 
    /// </summary>
    internal class TimeIntervalThreadCycleCounterBased : ITimeInterval
    {
        private readonly double cyclesPerSecond;

        private IntPtr handle;
        private ulong startCycles;
        private ulong stopCycles;
        private ulong elapsedCycles;
        private bool running;

        /// <summary>
        /// Obtain current time of stopwatch since last Stop method. You may call this from any thread.
        /// </summary>
        public TimeSpan Elapsed
        {
            get { return TimeSpan.FromSeconds(((double) elapsedCycles)/(cyclesPerSecond)); }
        }

        /// <summary>
        /// Create thread CPU timing object. You may call this from a thread outside the one you wish to measure.
        /// </summary>
        public TimeIntervalThreadCycleCounterBased()
        {
            // System call returns what seems to be (from measurements) a value that is in cycles per 1/1024 of a second (close to millisecond)
            long cyclesPerMillisecond;
            NativeMethods.QueryPerformanceFrequency(out cyclesPerMillisecond);
            cyclesPerSecond = (double) (1024.0*(double) cyclesPerMillisecond);

            handle = IntPtr.Zero;
            elapsedCycles = 0;
            startCycles = 0;
            stopCycles = 0;
            running = false;
        }

        /// <summary>
        /// Start measuring time thread is using CPU. Must invoke from thread to be measured!
        /// </summary>
        public void Start()
        {
            if (handle.Equals(IntPtr.Zero))
            {
                handle = NativeMethods.GetCurrentThread();
            }
            if (running) return;

            running = true;
            NativeMethods.QueryThreadCycleTime(handle, out startCycles);
        }

        /// <summary>
        /// Restart measuring time thread is using CPU. Must invoke from thread to be measured!
        /// </summary>
        public void Restart()
        {
            elapsedCycles = 0;
            Start();
        }

        /// <summary>
        /// Stop measuring time thread is using CPU. Must invoke from thread to be measured!
        /// </summary>
        public void Stop()
        {
            if (!running) return;

            running = false;
            NativeMethods.QueryThreadCycleTime(handle, out stopCycles);
            if (stopCycles > startCycles)
            {
                elapsedCycles += (stopCycles - startCycles);
            }
            else
            {
                var log = TraceLogger.GetLogger("Thread Cycle Counter", TraceLogger.LoggerType.Runtime);
                if (log.IsVerbose2)
                    log.Verbose2(0,
                        String.Format("Invalid cycle counts startCycles = {0}, stopCycles = {1}", startCycles,
                            stopCycles));

                // Some threadpool threads seem to be reset, this is normal with .NET threadpool threads as its assumed they are restart out of our control
                elapsedCycles += stopCycles;
            }
        }

        private static class NativeMethods
        {
            [DllImport("Kernel32.dll")]
            public static extern bool QueryThreadCycleTime(IntPtr handle, out ulong cycles);
            [DllImport("Kernel32.dll")]
            public static extern bool QueryPerformanceFrequency(out long lpFrequency);
            [DllImport("Kernel32.dll")]
            public static extern IntPtr GetCurrentThread();    
        }
    }
}
