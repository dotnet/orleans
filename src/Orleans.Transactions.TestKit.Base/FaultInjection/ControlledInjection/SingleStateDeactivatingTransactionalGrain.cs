using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.TestKit
{

    public interface IFaultInjectionTransactionTestGrain : IGrainWithGuidKey
    {
        [Transaction(TransactionOption.CreateOrJoin)]
        Task Set(int newValue);

        [Transaction(TransactionOption.CreateOrJoin)]
        Task Add(int numberToAdd, FaultInjectionControl faultInjectionControl = null);

        [Transaction(TransactionOption.CreateOrJoin)]
        Task<int> Get();

        Task Deactivate();
    }

    public class SingleStateFaultInjectionTransactionalGrain : Grain, IFaultInjectionTransactionTestGrain
    {
        private readonly IFaultInjectionTransactionalState<GrainData> data;
        private readonly ILoggerFactory loggerFactory;
        private ILogger logger;

        public SingleStateFaultInjectionTransactionalGrain(
            [FaultInjectionTransactionalState("data", TransactionTestConstants.TransactionStore)]
            IFaultInjectionTransactionalState<GrainData> data,
            ILoggerFactory loggerFactory)
        {
            this.data = data;
            this.loggerFactory = loggerFactory;
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            this.logger = this.loggerFactory.CreateLogger(this.GetGrainId().ToString());
            this.logger.LogInformation("GrainId {GrainId}", this.GetPrimaryKey());

            return base.OnActivateAsync(cancellationToken);
        }

        public Task Set(int newValue)
        {
            return this.data.PerformUpdate(d =>
            {
                this.logger.LogInformation("Setting value {NewValue}.", newValue);
                d.Value = newValue;
            });
        }

        public Task Add(int numberToAdd, FaultInjectionControl faultInjectionControl = null)
        {
            //reset in case control from last tx isn't cleared for some reason
            this.data.FaultInjectionControl.Reset();
            //dont replace it with this.data.FaultInjectionControl = faultInjectionControl, 
            //this.data.FaultInjectionControl must remain the same reference
            if (faultInjectionControl != null)
            {
                this.data.FaultInjectionControl.FaultInjectionPhase = faultInjectionControl.FaultInjectionPhase;
                this.data.FaultInjectionControl.FaultInjectionType = faultInjectionControl.FaultInjectionType;
            }
           
            return this.data.PerformUpdate(d =>
            {
                this.logger.LogInformation("Adding {NumberToAdd} to value {Value}.", numberToAdd, d.Value);
                d.Value += numberToAdd;
            });
        }

        public Task<int> Get()
        {
            return this.data.PerformRead<int>(d => d.Value);
        }

        public Task Deactivate()
        {
            this.DeactivateOnIdle();
            return Task.CompletedTask;
        }
    }
}
