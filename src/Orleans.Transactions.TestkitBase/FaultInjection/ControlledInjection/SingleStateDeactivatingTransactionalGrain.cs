﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
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

        public override Task OnActivateAsync()
        {
            this.logger = this.loggerFactory.CreateLogger(this.GetGrainIdentity().ToString());
            this.logger.LogInformation($"GrainId : {this.GetPrimaryKey()}.");

            return base.OnActivateAsync();
        }

        public Task Set(int newValue)
        {
            return this.data.PerformUpdate(d =>
            {
                this.logger.LogInformation($"Setting value {newValue}.");
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
                this.logger.LogInformation($"Adding {numberToAdd} to value {d.Value}.");
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
