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

        public override string Name
        {
            get { return $"ResponseWorkItem:Id={response.Id},Type={response.Result}"; }
        }

        public override IGrainContext GrainContext => this.target;

        public override void Execute()
        {
            try
            {
                RuntimeContext.SetExecutionContext(this.target);
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
