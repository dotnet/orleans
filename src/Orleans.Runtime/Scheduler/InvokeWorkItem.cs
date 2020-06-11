using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Orleans.Runtime.Scheduler
{
    internal class InvokeWorkItem : WorkItemBase
    {
        private readonly ActivationData activation;
        private readonly Message message;
        private readonly InsideRuntimeClient runtimeClient;
        private readonly ActivationMessageScheduler messsageScheduler;

        public InvokeWorkItem(ActivationData activation, Message message, InsideRuntimeClient runtimeClient, ActivationMessageScheduler messageScheduler)
        {
            if (activation?.GrainInstance == null)
            {
                ThrowMissingActivation(activation, message);
            }

            this.activation = activation;
            this.message = message;
            this.runtimeClient = runtimeClient;
            this.messsageScheduler = messageScheduler;
            activation.IncrementInFlightCount();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowMissingActivation(ActivationData activation, Message message) => throw new ArgumentException($"Creating InvokeWorkItem with bad activation: {activation}. Message: {message}");

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
                Task task = this.runtimeClient.Invoke(this.activation, this.message);

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
            catch
            {
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
            this.messsageScheduler.OnActivationCompletedRequest(activation, message);
        }

        public override string ToString()
        {
            return String.Format("{0} for activation={1} Message={2}", base.ToString(), activation, message);
        }
    }
}
