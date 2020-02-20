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

        public override WorkItemType ItemType { get { return WorkItemType.Request; } }

        public override string Name
        {
            get { return $"RequestWorkItem:Id={request.Id}"; }
        }

        public override void Execute()
        {
            target.HandleNewRequest(request);
        }

        public override string ToString()
        {
            return String.Format("{0}: {1} -> {2}", base.ToString(), target, request);
        }
    }
}
