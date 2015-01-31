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

﻿﻿using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;

namespace UnitTests.Tester
{
    public class UnitTestUtils
    {
        public static async Task WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout)
        {
            bool keepGoing = true;
            int numLoops = 0;
            // ReSharper disable AccessToModifiedClosure
            Func<Task> loop =
                async () =>
                {
                    do
                    {
                        numLoops++;
                        // need to wait a bit to before re-checking the condition.
                        await Task.Delay(TimeSpan.FromSeconds(1));
                    }
                    while (!await predicate() && keepGoing);
                };
            // ReSharper restore AccessToModifiedClosure

            var task = loop();
            try
            {
                await Task.WhenAny(new Task[] { task, Task.Delay(timeout) });
            }
            finally
            {
                keepGoing = false;
            }
            Assert.IsTrue(task.IsCompleted, "The test completed {0} loops then timed out after {1}", numLoops, timeout);
        }

        public static TimeSpan Multiply(TimeSpan time, double value)
        {
            double ticksD = checked(time.Ticks * value);
            long ticks = checked((long)ticksD);
            return TimeSpan.FromTicks(ticks);
        }
    }
}
