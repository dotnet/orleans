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

        public override WorkItemType ItemType { get { return WorkItemType.Response; } }

        public override string Name
        {
            get { return String.Format("ResponseWorkItem:Id={0},Type={1} {2}", response.Id, response.Result, response.DebugContext); }
        }

        public override void Execute()
        {
            try
            {
                RuntimeContext.SetExecutionContext(this.SchedulingContext);
                target.HandleResponse(response);
            }
            finally
            {
                RuntimeContext.ResetExecutionContext();
            }
        }

        public override string ToString()
        {
            return String.Format("{0}: Grain: {1} -> {2}", base.ToString(), target, response);
        }
    }
}
