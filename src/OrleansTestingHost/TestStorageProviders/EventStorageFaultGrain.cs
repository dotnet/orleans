
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Grain that tracks storage exceptions to be injected.
    /// </summary>
    public class EventStorageFaultGrain : Grain, IEventStorageFaultGrain
    {
        private Queue<Exception> faultSequence = new Queue<Exception>();

        /// <inheritdoc />
        public Task Add(params Exception[] exceptions)
        {
            foreach (var e in exceptions)
                faultSequence.Enqueue(e);
            return TaskDone.Done;
        }


        /// <inheritdoc />
        public Task Clear()
        {
            faultSequence.Clear();
            return TaskDone.Done;
        }

        /// <inheritdoc />
        public Task Next()
        {    
            var next = (faultSequence.Count == 0) ? null : faultSequence.Dequeue();
        
            if (next != null)
                throw next;

            return TaskDone.Done;
        }
    }
}
