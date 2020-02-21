using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Scheduler
{
    internal class InvokeWorkItem : WorkItemBase
    {
        private readonly ILogger logger;
        private readonly ActivationData activation;
        private readonly Message message;
        private readonly Dispatcher dispatcher;

        public InvokeWorkItem(ActivationData activation, Message message, Dispatcher dispatcher, ILogger logger)
        {
            this.logger = logger;
            if (activation?.GrainInstance == null)
            {
                var str = string.Format("Creating InvokeWorkItem with bad activation: {0}. Message: {1}", activation, message);
                logger.Warn(ErrorCode.SchedulerNullActivation, str);
                throw new ArgumentException(str);
            }

            this.activation = activation;
            this.message = message;
            this.dispatcher = dispatcher;
            activation.IncrementInFlightCount();
        }

        public override WorkItemType ItemType
        {
            get { return WorkItemType.Invoke; }
        }

        public override string Name
        {
            get { return  $"InvokeWorkItem:Id={message.Id}"; }
        }

        public override IGrainContext GrainContext => this.activation;

        public override void Execute()
        {
            try
            {
                RuntimeContext.SetExecutionContext(this.activation);
                var grain = activation.GrainInstance;
                var runtimeClient = this.dispatcher.RuntimeClient;
                Task task = runtimeClient.Invoke(grain, this.activation, this.message);

                // Note: This runs for all outcomes of resultPromiseTask - both Success or Fault
                if (task.IsCompleted)
                {
                    OnComplete();
                }
                else
                {
                    task.ContinueWith(t => OnComplete()).Ignore();
                }
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.InvokeWorkItem_UnhandledExceptionInInvoke, 
                    String.Format("Exception trying to invoke request {0} on activation {1}.", message, activation), exc);
                OnComplete();
            }
            finally
            {
                RuntimeContext.ResetExecutionContext();
            }
        }

        private void OnComplete()
        {
            activation.DecrementInFlightCount();
            this.dispatcher.OnActivationCompletedRequest(activation, message);
        }

        public override string ToString()
        {
            return String.Format("{0} for activation={1} Message={2}", base.ToString(), activation, message);
        }
    }
}
