using System;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.Serialization;
using Azure;
using Microsoft.Extensions.Logging;

namespace Orleans.Transactions.TestKit
{
    public partial class SimpleAzureStorageExceptionInjector : IControlledTransactionFaultInjector, ITransactionScopedFaultInjector
    {
        private readonly object lockObj = new();
        private Guid? targetTransactionId;
        public bool InjectBeforeStore { get; set; }
        public bool InjectAfterStore { get; set; }
        public bool InjectGenericAfterStore { get; set; }
        private int injectionBeforeStoreCounter = 0;
        private int injectionAfterStoreCounter = 0;
        private int genericInjectionAfterStoreCounter = 0;
        private readonly ILogger logger;
        public SimpleAzureStorageExceptionInjector(ILogger<SimpleAzureStorageExceptionInjector> logger)
        {
            this.logger = logger;
        }

        public void AfterStore()
        {
            this.AfterStore(default);
        }

        void ITransactionScopedFaultInjector.Arm(
            Guid transactionId,
            FaultInjectionType injectionType,
            bool requireTransactionMatch)
        {
            lock (this.lockObj)
            {
                this.targetTransactionId = requireTransactionMatch ? transactionId : null;
                this.InjectBeforeStore = injectionType == FaultInjectionType.ExceptionBeforeStore;
                this.InjectAfterStore = injectionType == FaultInjectionType.ExceptionAfterStore;
                this.InjectGenericAfterStore = injectionType == FaultInjectionType.GenericExceptionAfterStore;
            }
        }

        void ITransactionScopedFaultInjector.BeforeStore(ImmutableArray<Guid> transactionIds)
            => this.BeforeStore(transactionIds);

        void ITransactionScopedFaultInjector.AfterStore(ImmutableArray<Guid> transactionIds)
            => this.AfterStore(transactionIds);

        private void AfterStore(ImmutableArray<Guid> transactionIds)
        {
            lock (this.lockObj)
            {
                if (!this.IsTargetStore(transactionIds))
                {
                    return;
                }

                if (this.InjectAfterStore)
                {
                    this.InjectAfterStore = false;
                    this.targetTransactionId = null;
                    this.injectionAfterStoreCounter++;
                    var message = $"Storage exception thrown after store, thrown total {injectionAfterStoreCounter}";
                    LogInformationMessage(this.logger, message);
                    throw new SimpleAzureStorageException(message);
                }

                if (this.InjectGenericAfterStore)
                {
                    this.InjectGenericAfterStore = false;
                    this.targetTransactionId = null;
                    this.genericInjectionAfterStoreCounter++;
                    var message = $"Generic storage exception thrown after store, thrown total {genericInjectionAfterStoreCounter}";
                    LogInformationMessage(this.logger, message);
                    throw new InvalidOperationException(message);
                }
            }
        }

        public void BeforeStore()
        {
            this.BeforeStore(default);
        }

        private void BeforeStore(ImmutableArray<Guid> transactionIds)
        {
            lock (this.lockObj)
            {
                if (!this.IsTargetStore(transactionIds) || !this.InjectBeforeStore)
                {
                    return;
                }

                this.InjectBeforeStore = false;
                this.targetTransactionId = null;
                this.injectionBeforeStoreCounter++;
                var message = $"Storage exception thrown before store. Thrown total {injectionBeforeStoreCounter}";
                LogInformationMessage(this.logger, message);
                throw new SimpleAzureStorageException(message);
            }
        }

        private bool IsTargetStore(ImmutableArray<Guid> transactionIds)
            => this.targetTransactionId is not { } targetTransactionId
                || (!transactionIds.IsDefaultOrEmpty && transactionIds.IndexOf(targetTransactionId) >= 0);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "{Message}"
        )]
        private static partial void LogInformationMessage(ILogger logger, string message);
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

        [Obsolete("TThe serialization constructor pattern was made obsolete in modern versions of .NET. Use the other constructors instead.")]
        [EditorBrowsable(EditorBrowsableState.Never)]
        protected SimpleAzureStorageException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
