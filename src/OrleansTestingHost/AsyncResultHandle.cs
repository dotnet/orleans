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
using System.Threading.Tasks;

namespace Orleans.TestingHost
{
    /// <summary>
    /// This class is for internal testing use only.
    /// </summary>
    public class AsyncResultHandle : MarshalByRefObject
    {
        bool done = false;
        bool continueFlag = false;

        public virtual void Reset()
        {
            Exception = null;
            Result = null;
            done = false;
            continueFlag = false;
        }

        public bool Done
        {
            get { return done; }
            set { done = value; }
        }

        public bool Continue
        {
            get { return continueFlag; }
            set { continueFlag = value; }
        }

        public Exception Exception { get; set; }

        public object Result { get; set; }

        /// <summary>
        /// </summary>
        /// <param name="timeout"></param>
        /// <returns>Returns <c>true</c> if operation completes before timeout</returns>
        public Task<bool> WaitForFinished(TimeSpan timeout)
        {
            return WaitFor(timeout, () => done);
        }

        /// <summary>
        /// </summary>
        /// <param name="timeout"></param>
        /// <returns>Returns <c>true</c> if operation completes before timeout</returns>
        public Task<bool> WaitForContinue(TimeSpan timeout)
        {
            return WaitFor(timeout, () => continueFlag);
        }

        /// <summary>
        /// </summary>
        /// <param name="timeout"></param>
        /// <param name="checkFlag"></param>
        /// <returns>Returns <c>true</c> if operation completes before timeout</returns>
        public async Task<bool> WaitFor(TimeSpan timeout, Func<bool> checkFlag)
        {
            double remaining = timeout.TotalMilliseconds;
            while (!checkFlag())
            {
                if (remaining < 0)
                {
                    //throw new TimeoutException("Timeout waiting for result for " + timeout);
                    return false;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200));
                remaining -= 200;
            }

            return true;
        }
    }
}
