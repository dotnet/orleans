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
using System.Diagnostics;

namespace Orleans.Runtime
{
    internal interface ITimeInterval
    {
        void Start();

        void Stop();

        void Restart();

        TimeSpan Elapsed { get; }
    }

    internal static class TimeIntervalFactory
    {
        public static ITimeInterval CreateTimeInterval(bool measureFineGrainedTime)
        {
            return measureFineGrainedTime
                ? (ITimeInterval) new TimeIntervalStopWatchBased()
                : new TimeIntervalDateTimeBased();
        }
    }

    internal class TimeIntervalStopWatchBased : ITimeInterval
    {
        private readonly Stopwatch stopwatch;

        public TimeIntervalStopWatchBased()
        {
            stopwatch = new Stopwatch();
        }

        public void Start()
        {
            stopwatch.Start();
        }

        public void Stop()
        {
            stopwatch.Stop();
        }

        public void Restart()
        {
            stopwatch.Restart();

        }
        public TimeSpan Elapsed { get { return stopwatch.Elapsed; } }
    }

    internal class TimeIntervalDateTimeBased : ITimeInterval
    {
        private bool running;
        private DateTime start;

        public TimeSpan Elapsed { get; private set; }

        public TimeIntervalDateTimeBased()
        {
            running = false;
            Elapsed = TimeSpan.Zero;
        }

        public void Start()
        {
            if (running) return;

            start = DateTime.UtcNow;
            running = true;
            Elapsed = TimeSpan.Zero;
        }

        public void Stop()
        {
            if (!running) return;

            var end = DateTime.UtcNow;
            Elapsed += (end - start);
            running = false;
        }

        public void Restart()
        {
            start = DateTime.UtcNow;
            running = true;
            Elapsed = TimeSpan.Zero;
        }
    }
}
