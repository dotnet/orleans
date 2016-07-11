using System;

namespace Orleans.Runtime.Scheduler
{
    internal class RequestWorkItem : WorkItemBase
    {
        private readonly Message request;
        private readonly SystemTarget target;

        public RequestWorkItem(SystemTarget t, Message m)
        {
            target = t;
            request = m;
        }

        #region IWorkItem Members

        public override WorkItemType ItemType { get { return WorkItemType.Request; } }

        public override string Name
        {
            get { return String.Format("RequestWorkItem:Id={0} {1}", request.Id, request.DebugContext); }
        }

        public override void Execute()
        {
            target.HandleNewRequest(request);
        }

        #endregion

        public override string ToString()
        {
            return String.Format("{0}: {1} -> {2}", base.ToString(), target, request);
        }
    }
}
