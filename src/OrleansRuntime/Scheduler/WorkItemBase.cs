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

namespace Orleans.Runtime.Scheduler
{
    internal abstract class WorkItemBase : IWorkItem
    {

        internal protected WorkItemBase()
        {
        }

        public ISchedulingContext SchedulingContext { get; set; }
        public TimeSpan TimeSinceQueued 
        {
            get { return Utils.Since(TimeQueued); } 
        }

        public abstract string Name { get; }

        public abstract WorkItemType ItemType { get; }

        public DateTime TimeQueued { get; set; }

        public abstract void Execute();

        public bool IsSystem
        {
            get { return SchedulingUtils.IsSystemContext(this.SchedulingContext); }
        }

        public override string ToString()
        {
            return String.Format("[{0} WorkItem Name={1}, Ctx={2}]", 
                ItemType, 
                Name ?? "",
                (SchedulingContext == null) ? "null" : SchedulingContext.ToString()
            );
        }
    }
}

