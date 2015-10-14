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
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.TestingHost
{
    public static class TestingUtils
    {
        public static async Task WaitUntilAsync(Func<bool,Task<bool>> predicate, TimeSpan timeout)
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
                    while (!await predicate(!keepGoing) && keepGoing);
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
            await task;
        }

        public static TimeSpan Multiply(TimeSpan time, double value)
        {
            double ticksD = checked(time.Ticks * value);
            long ticks = checked((long)ticksD);
            return TimeSpan.FromTicks(ticks);
        }

        public static void ConfigureThreadPoolSettingsForStorageTests(int numDotNetPoolThreads = 200)
        {
            ThreadPool.SetMinThreads(numDotNetPoolThreads, numDotNetPoolThreads);
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.DefaultConnectionLimit = numDotNetPoolThreads; // 1000;
            ServicePointManager.UseNagleAlgorithm = false;
        }

        public static async Task WithTimeout(this Task taskToComplete, TimeSpan timeout, string message)
        {
            if (taskToComplete.IsCompleted)
            {
                await taskToComplete;
                return;
            }

            await Task.WhenAny(taskToComplete, Task.Delay(timeout));

            // We got done before the timeout, or were able to complete before this code ran, return the result
            if (taskToComplete.IsCompleted)
            {
                // Await this so as to propagate the exception correctly
                await taskToComplete;
                return;
            }

            // We did not complete before the timeout, we fire and forget to ensure we observe any exceptions that may occur
            taskToComplete.Ignore();
            throw new TimeoutException(message);
        }

        public static T RoundTripDotNetSerializer<T>(T input)
        {
            IFormatter formatter = new BinaryFormatter();
            MemoryStream stream = new MemoryStream(new byte[100000], true);
            formatter.Serialize(stream, input);
            stream.Position = 0;
            T output = (T)formatter.Deserialize(stream);
            return output;
        }
    }
}
