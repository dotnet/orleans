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
    internal class InterlockedExchangeLock
    {
        private const int Locked = 1;
        private const int Unlocked = 0;
        private int lockState = Unlocked;

        public bool TryGet()
        {
            return Interlocked.Exchange(ref lockState, Locked) != Locked;
        }
        
        public void Get()
        {
            if (TryGet())
                return;

            SpinWait spinWait = new SpinWait();
            while (!TryGet())
            {
                spinWait.SpinOnce();
            }
        }
        
        public void Release()
        {
            Interlocked.Exchange(ref lockState, Unlocked);
        }
    }
}
