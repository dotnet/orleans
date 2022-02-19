using System;
using System.Runtime.Serialization;
using Azure;
using Microsoft.Extensions.Logging;

namespace Orleans.Transactions.TestKit
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
                var message = $"Storage exception thrown after store, thrown total {injectionAfterStoreCounter}";
                this.logger.LogInformation(message);
                throw new SimpleAzureStorageException(message);
            }
        }

        public void BeforeStore()
        {
            if (InjectBeforeStore)
            {
                InjectBeforeStore = false;
                this.injectionBeforeStoreCounter++;
                var message = $"Storage exception thrown before store. Thrown total {injectionBeforeStoreCounter}";
                this.logger.LogInformation(message);
                throw new SimpleAzureStorageException(message);
            }
        }
    }

    [GenerateSerializer]
    public class SimpleAzureStorageException : RequestFailedException
    {
        public SimpleAzureStorageException(string message) : base(message)
        {
        }

        public SimpleAzureStorageException(string message, Exception innerException) : base(message, innerException)
        {
        }

        public SimpleAzureStorageException(int status, string message) : base(status, message)
        {
        }

        public SimpleAzureStorageException(int status, string message, Exception innerException) : base(status, message, innerException)
        {
        }

        public SimpleAzureStorageException(int status, string message, string errorCode, Exception innerException) : base(status, message, errorCode, innerException)
        {
        }

        protected SimpleAzureStorageException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
