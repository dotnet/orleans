﻿using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Orleans.Transactions.Tests.FaultInjection;

namespace Orleans.Transactions.Azure.Tests
{
    public class SimpleAzureStorageExceptionInjector : IControlledTransactionFaultInjector
    {
        public bool InjectBeforeStore { get; set; }
        public bool InjectAfterStore { get; set; }
        private int injectionBeforeStoreCounter = 0;
        private int injectionAfterStoreCounter = 0;
        private ILogger logger;
        public SimpleAzureStorageExceptionInjector(ILogger<SimpleAzureStorageExceptionInjector> logger)
        {
            this.logger = logger;
        }

        public void AfterStore()
        {
            if (InjectAfterStore)
            {
                InjectAfterStore = false;
                this.injectionAfterStoreCounter++;
                this.logger.LogInformation($"Storage exception thrown after store, thrown total {injectionAfterStoreCounter}");
                throw new SimpleAzureStorageException();
            }
        }

        public void BeforeStore()
        {
            if (InjectBeforeStore)
            {
                InjectBeforeStore = false;
                this.injectionBeforeStoreCounter++;
                this.logger.LogInformation($"Storage exception thrown before store. Thrown total {injectionBeforeStoreCounter}");
                throw new SimpleAzureStorageException();
            }
        }
    }

    public class SimpleAzureStorageException : StorageException
    {
    }
}
