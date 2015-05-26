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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Orleans.Runtime
{
    internal class RuntimeQueue<T> : IDisposable
    {
        private BlockingCollection<T> queue;
        private List<T> list;
        private readonly object lockable;
        public bool IsAddingCompleted { get; private set; }

        public RuntimeQueue()
        {
            if (Constants.USE_BLOCKING_COLLECTION)
            {
                queue = new BlockingCollection<T>();
                IsAddingCompleted = false;
            }
            else
            {
                lockable = new object();
                list = new List<T>();
                IsAddingCompleted = false;
            }
        }

        public void Add(T item)
        {
            if (Constants.USE_BLOCKING_COLLECTION)
            {
                queue.Add(item);
            }
            else
            {
                if(IsAddingCompleted)
                    throw new InvalidOperationException("IsAddingCompleted.");
                bool lockTaken = false;
                try
                {
                    Monitor.Enter(lockable, ref lockTaken);
                    if (IsAddingCompleted)
                        throw new InvalidOperationException("IsAddingCompleted.");    
                    list.Add(item);
                    Monitor.PulseAll(lockable);
                }
                finally
                {
                    if (lockTaken)
                        Monitor.Exit(lockable);
                }
            }
        }

        public bool TryTake(out T item)
        {
            if (Constants.USE_BLOCKING_COLLECTION) return queue.TryTake(out item);

            bool lockTaken = false;
            try
            {
                Monitor.Enter(lockable, ref lockTaken);
                {
                    if (list.Count > 0)
                    {
                        item = list[0];
                        list.RemoveAt(0);
                        return true;
                    }
                    else
                    {
                        item = default(T);
                        return false;
                    }
                }
            }
            finally
            {
                if (lockTaken)
                    Monitor.Exit(lockable);
            }
        }

        public T Take()
        {
            if (Constants.USE_BLOCKING_COLLECTION) return queue.Take();

            while (true)
            {
                bool lockTaken = false;
                try
                {
                    Monitor.Enter(lockable, ref lockTaken);
                    {
                        if (list.Count > 0)
                        {
                            T item = list[0];
                            list.RemoveAt(0);
                            return item;
                        }
                        else if (IsAddingCompleted)
                        {
                            throw new InvalidOperationException("IsAddingCompleted and the queue is empty.");
                        }
                        else
                        {
                            Monitor.Wait(lockable);
                            continue; // loop and try again.
                        }
                    }
                }
                finally
                {
                    if (lockTaken)
                        Monitor.Exit(lockable);
                }
            }
        }

        public T First()
        {
            if (Constants.USE_BLOCKING_COLLECTION) return queue.First();

            while (true)
            {
                bool lockTaken = false;
                try
                {
                    Monitor.Enter(lockable, ref lockTaken);
                    {
                        if (list.Count > 0) return list[0];

                        if (IsAddingCompleted) throw new InvalidOperationException("IsAddingCompleted and the queue is empty.");
                        
                        Monitor.Wait(lockable);
                        continue; // loop and try again.
                    }
                }
                finally
                {
                    if (lockTaken)
                        Monitor.Exit(lockable);
                }
            }
        }

        public void CompleteAdding()
        {
            if (Constants.USE_BLOCKING_COLLECTION)
            { 
                queue.CompleteAdding();
            }
            else
            {
                lock (lockable)
                {
                    IsAddingCompleted = true;
                }
            }
        }

        public int Count
        {
            get
            {
                if (Constants.USE_BLOCKING_COLLECTION) return queue.Count;
                
                lock (lockable)
                {
                    return list.Count;
                }
            }
        }

        public void Dispose()
        {
            if (Constants.USE_BLOCKING_COLLECTION)
            {
                queue.Dispose();
            }
            else
            {
                lock (lockable)
                {
                    IsAddingCompleted = true;
                    list = null;
                }
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;

            if (Constants.USE_BLOCKING_COLLECTION)
            {
                if (queue == null) return;

                queue.Dispose();
                queue = null;
            }
            else
            {
                lock (lockable)
                {
                    IsAddingCompleted = true;
                    list = null;
                }
            }
        }
    }
}
