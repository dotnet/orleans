using System;
using System.Threading.Tasks;

namespace Orleans.Runtime.Scheduler
{
    internal class InvokeWorkItem : WorkItemBase
    {
        private static readonly Logger logger = LogManager.GetLogger("InvokeWorkItem", LoggerType.Runtime);
        private readonly ActivationData activation;
        private readonly Message message;
        private readonly Dispatcher dispatcher;

        public InvokeWorkItem(ActivationData activation, Message message, ISchedulingContext context, Dispatcher dispatcher)
        {
            this.activation = activation;
            this.message = message;
            this.dispatcher = dispatcher;
            SchedulingContext = context;
            if (activation == null || activation.GrainInstance==null)
            {
                var str = String.Format("Creating InvokeWorkItem with bad activation: {0}. Message: {1}", activation, message);
                logger.Warn(ErrorCode.SchedulerNullActivation, str);
                throw new ArgumentException(str);
            }
            activation.IncrementInFlightCount();
        }

        #region Implementation of IWorkItem

        public override WorkItemType ItemType
        {
            get { return WorkItemType.Invoke; }
        }

        public override string Name
        {
            get { return String.Format("InvokeWorkItem:Id={0} {1}", message.Id, message.DebugContext); }
        }

        public override void Execute()
        {
            try
            {
                IAddressable grain = activation.GrainInstance;
                Task task = InsideRuntimeClient.Current.Invoke(grain, activation, message);
                task.ContinueWith(t =>
                {
                    // Note: This runs for all outcomes of resultPromiseTask - both Success or Fault
                    activation.DecrementInFlightCount();
                    this.dispatcher.OnActivationCompletedRequest(activation, message);
                }).Ignore();
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.InvokeWorkItem_UnhandledExceptionInInvoke, 
                    String.Format("Exception trying to invoke request {0} on activation {1}.", message, activation), exc);

                activation.DecrementInFlightCount();
                this.dispatcher.OnActivationCompletedRequest(activation, message);
            }
        }

        #endregion

        public override string ToString()
        {
            return String.Format("{0} for activation={1} Message={2}", base.ToString(), activation, message);
        }
    }
}
