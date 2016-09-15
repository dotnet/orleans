using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    // About 2x faster than ordinary lock on mostly single threaded locking
    internal class FastLock
    {
        private int lockTaken = 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Take()
        {
            if (Interlocked.CompareExchange(ref lockTaken, 1, 0) == 0)
                return;

            SpinWait spinWait = new SpinWait();
            while (Interlocked.CompareExchange(ref lockTaken, 1, 0) != 0)
            {
                spinWait.SpinOnce();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Release()
        {
            Interlocked.Exchange(ref lockTaken, 0);
        }
    }
}
