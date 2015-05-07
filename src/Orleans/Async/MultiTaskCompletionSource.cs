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

namespace Orleans
{
    internal class MultiTaskCompletionSource
    {
        private readonly TaskCompletionSource<bool> tcs;
        private int count;
        private readonly object lockable;

        public MultiTaskCompletionSource(int count)
        {
            if (count <= 0)
            {
                throw new ArgumentOutOfRangeException("count", "count has to be positive.");
            }
            tcs = new TaskCompletionSource<bool>();
            this.count = count;
            lockable = new object();
        }

        public Task Task
        {
            get { return tcs.Task; }
        }


        public void SetOneResult()
        {
            lock (lockable)
            {
                if (count <= 0)
                {
                    throw new InvalidOperationException("SetOneResult was called more times than initialy specified by the count argument.");
                }
                count--;
                if (count == 0)
                {
                    tcs.SetResult(true);
                }
            }
        }

        public void SetMultipleResults(int num)
        {
            lock (lockable)
            {
                if (num <= 0)
                {
                    throw new ArgumentOutOfRangeException("num", "num has to be positive.");
                }
                if (count - num < 0)
                {
                    throw new ArgumentOutOfRangeException("num", "num is too large, count - num < 0.");
                }
                count = count - num;
                if (count == 0)
                {
                    tcs.SetResult(true);
                }
            }
        }
    }
}
