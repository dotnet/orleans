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

namespace Orleans.Runtime
{
    internal class RuntimeContext
    {
        public TaskScheduler Scheduler { get; private set; }
        public ISchedulingContext ActivationContext { get; private set; }

        [ThreadStatic]
        private static RuntimeContext context;
        public static RuntimeContext Current 
        { 
            get { return context; } 
        }

        internal static ISchedulingContext CurrentActivationContext
        {
            get { return RuntimeContext.Current != null ? RuntimeContext.Current.ActivationContext : null; }
        }

        internal static void InitializeThread(TaskScheduler scheduler)
        {
            // There seems to be an implicit coupling of threads and contexts here that may be fragile. 
            // E.g. if InitializeThread() is mistakenly called on a wrong thread, would that thread be considered a worker pool thread from that point on? 
            // Is there a better/safer way to identify worker threads? 
            if (context != null && scheduler != null)
            {
                throw new InvalidOperationException("RuntimeContext.Current has already been initialized for this thread.");
            }
            context = new RuntimeContext {Scheduler = scheduler};
        }

        internal static void InitializeMainThread()
        {
            context = new RuntimeContext {Scheduler = null};
        }

        internal static void SetExecutionContext(ISchedulingContext shedContext, TaskScheduler scheduler)
        {
            if (context == null) throw new InvalidOperationException("SetExecutionContext called on unexpected non-WorkerPool thread");
            context.ActivationContext = shedContext;
            context.Scheduler = scheduler;
        }

        internal static void ResetExecutionContext()
        {
            context.ActivationContext = null;
            context.Scheduler = null;
        }

        public override string ToString()
        {
            return String.Format("RuntimeContext: Activation={0}, Scheduler={1}", 
                ActivationContext != null ? ActivationContext.ToString() : "null",
                Scheduler != null ? Scheduler.ToString() : "null");
        }
    }
}
