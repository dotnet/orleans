using System;

namespace Orleans.Runtime.Scheduler
{
    internal class RequestWorkItem : WorkItemBase
    {
        public static readonly Action<object> ExecuteAction = state => ((RequestWorkItem)state).Execute();

        private readonly Message request;
        private readonly SystemTarget target;

        public RequestWorkItem(SystemTarget t, Message m)
        {
            target = t;
            request = m;
        }

        public override string Name
        {
            get { return $"RequestWorkItem:Id={request.Id}"; }
        }

        public override IGrainContext GrainContext => this.target;

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
