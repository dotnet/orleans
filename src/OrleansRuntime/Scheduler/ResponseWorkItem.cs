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
    internal class ResponseWorkItem : WorkItemBase
    {
        private readonly Message response;
        private readonly SystemTarget target;

        public ResponseWorkItem(SystemTarget t, Message m)
        {
            target = t;
            response = m;
        }

        #region IWorkItem Members

        public override WorkItemType ItemType { get { return WorkItemType.Response; } }

        public override string Name
        {
            get { return String.Format("ResponseWorkItem:Id={0},Type={1} {2}", response.Id, response.Result, response.DebugContext); }
        }

        public override void Execute()
        {
            if (Message.WriteMessagingTraces) response.AddTimestamp(Message.LifecycleTag.DequeueWorkItem);
            target.HandleResponse(response);
        }

        #endregion

        public override string ToString()
        {
            return String.Format("{0}: Grain: {1} -> {2}", base.ToString(), target, response);
        }
    }
}
