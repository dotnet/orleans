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
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
