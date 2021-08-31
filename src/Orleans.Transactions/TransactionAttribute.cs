using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;
using Orleans.Transactions;

namespace Orleans
{
    /// <summary>
    /// The TransactionAttribute attribute is used to mark methods that start and join transactions.
    /// </summary>
    [InvokableCustomInitializer("SetTransactionOptions")]
    [InvokableBaseType(typeof(GrainReference), typeof(ValueTask), typeof(TransactionRequest))]
    [InvokableBaseType(typeof(GrainReference), typeof(ValueTask<>), typeof(TransactionRequest<>))]
    [InvokableBaseType(typeof(GrainReference), typeof(Task), typeof(TransactionTaskRequest))]
    [InvokableBaseType(typeof(GrainReference), typeof(Task<>), typeof(TransactionTaskRequest<>))]
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TransactionAttribute : Attribute
    {
        public TransactionAttribute(TransactionOption requirement)
        {
            Requirement = requirement;
            ReadOnly = false;
        }

        public TransactionAttribute(TransactionOptionAlias alias)
        {
            Requirement = (TransactionOption)(int)alias;
            ReadOnly = false;
        }

        public TransactionOption Requirement { get; }
        public bool ReadOnly { get; set; }
    }

    public enum TransactionOption
    {
        Suppress,     // Logic is not transactional but can be called from within a transaction.  If called within the context of a transaction, the context will not be passed to the call.
        CreateOrJoin, // Logic is transactional.  If called within the context of a transaction, it will use that context, else it will create a new context.
        Create,       // Logic is transactional and will always create a new transaction context, even if called within an existing transaction context.
        Join,         // Logic is transactional but can only be called within the context of an existing transaction.
        Supported,    // Logic is not transactional but supports transactions.  If called within the context of a transaction, the context will be passed to the call.
        NotAllowed    // Logic is not transactional and cannot be called from within a transaction.  If called within the context of a transaction, it will throw a not supported exception.
    }

    public enum TransactionOptionAlias
    {
        Suppress     = TransactionOption.Supported,
        Required     = TransactionOption.CreateOrJoin,
        RequiresNew  = TransactionOption.Create,
        Mandatory    = TransactionOption.Join,
        Never        = TransactionOption.NotAllowed,
    }

    [GenerateSerializer]
    public abstract class TransactionRequestBase : RequestBase, IOutgoingGrainCallFilter, IOnDeserialized
    {
        [NonSerialized]
        private Serializer<OrleansTransactionAbortedException> _serializer;

        [NonSerialized]
        private ITransactionAgent _transactionAgent;

        private ITransactionAgent TransactionAgent => _transactionAgent ?? throw new OrleansTransactionsDisabledException();

        [Id(1)]
        public TransactionOption TransactionOption { get; set; }

        [Id(2)]
        public TransactionInfo TransactionInfo { get; set; }

        [GeneratedActivatorConstructor]
        protected TransactionRequestBase(Serializer<OrleansTransactionAbortedException> exceptionSerializer, IServiceProvider serviceProvider)
        {
            _serializer = exceptionSerializer;

            // May be null, eg on an external client. We will throw if it's null at the time of invocation.
            _transactionAgent = serviceProvider.GetService<ITransactionAgent>();
        }

        public bool IsAmbientTransactionSuppressed => TransactionOption switch
        {
            TransactionOption.Create => true,
            TransactionOption.Suppress => true,
            _ => false
        };

        public bool IsTransactionRequired => TransactionOption switch
        {
            TransactionOption.Create => true,
            TransactionOption.CreateOrJoin => true,
            TransactionOption.Join => true,
            _ => false
        };

        protected void SetTransactionOptions(TransactionOptionAlias txOption) => SetTransactionOptions((TransactionOption)txOption);

        protected void SetTransactionOptions(TransactionOption txOption)
        {
            this.TransactionOption = txOption;
        }

        async Task IOutgoingGrainCallFilter.Invoke(IOutgoingGrainCallContext context)
        {
            var transactionInfo = SetTransactionInfo();
            try
            {
                await context.Invoke();
            }
            finally
            {
                var returnedTransactionInfo = (context.Response as TransactionResponse)?.TransactionInfo;
                if (transactionInfo is { } && returnedTransactionInfo is { })
                {
                    transactionInfo.Join(returnedTransactionInfo);
                }
            }
        }

        private TransactionInfo SetTransactionInfo()
        { 
            // Clear transaction info if transaction operation requires new transaction.
            var transactionInfo = TransactionContext.GetTransactionInfo();

            // Enforce join transaction calls
            if (TransactionOption == TransactionOption.Join && transactionInfo == null)
            {
                throw new NotSupportedException("Call cannot be made outside of a transaction.");
            }

            // Enforce not allowed transaction calls
            if (TransactionOption == TransactionOption.NotAllowed && transactionInfo != null)
            {
                throw new NotSupportedException("Call cannot be made within a transaction.");
            }

            // Clear transaction context if creating a transaction or transaction is suppressed
            if (TransactionOption is TransactionOption.Create or TransactionOption.Suppress)
            {
                transactionInfo = null;
            }

            if (transactionInfo == null)
            {
                // if we're leaving a transaction context, make sure it's been cleared from the request context.
                TransactionContext.Clear();
            }
            else
            {
                this.TransactionInfo = transactionInfo?.Fork();
            }

            return transactionInfo;
        }

        public override async ValueTask<Response> Invoke()
        {
            Response response;
            var transactionInfo = this.TransactionInfo;
            bool startedNewTransaction = false;
            try
            {
                if (IsTransactionRequired && transactionInfo == null)
                {
                    // TODO: this should be a configurable parameter
                    var transactionTimeout = Debugger.IsAttached ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(10);

                    // Start a new transaction
                    var isReadOnly = (this.Options | InvokeMethodOptions.ReadOnly) == InvokeMethodOptions.ReadOnly;
                    transactionInfo = await TransactionAgent.StartTransaction(isReadOnly, transactionTimeout);
                    startedNewTransaction = true;
                }

                TransactionContext.SetTransactionInfo(transactionInfo);
                response = await BaseInvoke();
            }
            catch (Exception exception)
            {
                response = Response.FromException(exception);
            }
            finally
            {
                TransactionContext.Clear();
            }

            if (transactionInfo != null)
            {
                transactionInfo.ReconcilePending();

                if (response.Exception is { } invokeException)
                {
                    // Record reason for abort, if not already set.
                    transactionInfo.RecordException(invokeException, _serializer);
                }

                OrleansTransactionException transactionException = transactionInfo.MustAbort(_serializer);

                // This request started the transaction, so we try to commit before returning,
                // or if it must abort, tell participants that it aborted
                if (startedNewTransaction)
                {
                    if (transactionException is not null)
                    {
                        await TransactionAgent.Abort(transactionInfo);
                    }
                    else
                    {
                        var (status, exception) = await TransactionAgent.Resolve(transactionInfo);
                        if (status != TransactionalStatus.Ok)
                        {
                            transactionException = status.ConvertToUserException(transactionInfo.Id, exception);
                        }
                    }
                }

                if (transactionException != null)
                {
                    response = Response.FromException(transactionException);
                }

                response = TransactionResponse.Create(response, transactionInfo);
            }

            return response;
        }

        protected abstract ValueTask<Response> BaseInvoke();

        public override void Dispose()
        {
           TransactionInfo = null;
        }

        void IOnDeserialized.OnDeserialized(DeserializationContext context)
        {
            _serializer = context.ServiceProvider.GetRequiredService<Serializer<OrleansTransactionAbortedException>>();
            _transactionAgent = context.ServiceProvider.GetRequiredService<ITransactionAgent>();
        }
    }

    [GenerateSerializer]
    public sealed class TransactionResponse : Response
    {
        [Id(0)]
        private Response _response;

        [Id(1)]
        public TransactionInfo TransactionInfo { get; set; }

        public static TransactionResponse Create(Response response, TransactionInfo transactionInfo)
        {
            return new TransactionResponse
            {
                _response = response,
                TransactionInfo = transactionInfo
            };
        }

        public override object Result { get => _response.Result; set => _response.Result = value; }
        public override Exception Exception { get => _response.Exception; set => _response.Exception = value; }
        public override void Dispose()
        {
            TransactionInfo = null;
            _response.Dispose();
        }

        public override T GetResult<T>() => _response.GetResult<T>();
    }

    [GenerateSerializer]
    public abstract class TransactionRequest : TransactionRequestBase 
    {
        [GeneratedActivatorConstructor]
        protected TransactionRequest(Serializer<OrleansTransactionAbortedException> exceptionSerializer, IServiceProvider serviceProvider) : base(exceptionSerializer, serviceProvider)
        {
        }

        protected override ValueTask<Response> BaseInvoke()
        {
            try
            {
                var resultTask = InvokeInner();
                if (resultTask.IsCompleted)
                {
                    resultTask.GetAwaiter().GetResult();
                    return new ValueTask<Response>(Response.Completed);
                }

                return CompleteInvokeAsync(resultTask);
            }
            catch (Exception exception)
            {
                return new ValueTask<Response>(Response.FromException(exception));
            }
        }

        private static async ValueTask<Response> CompleteInvokeAsync(ValueTask resultTask)
        {
            try
            {
                await resultTask;
                return Response.Completed;
            }
            catch (Exception exception)
            {
                return Response.FromException(exception);
            }
        }

        // Generated
        protected abstract ValueTask InvokeInner();
    }

    [GenerateSerializer]
    public abstract class TransactionRequest<TResult> : TransactionRequestBase
    {
        [GeneratedActivatorConstructor]
        protected TransactionRequest(Serializer<OrleansTransactionAbortedException> exceptionSerializer, IServiceProvider serviceProvider) : base(exceptionSerializer, serviceProvider)
        {
        }

        protected override ValueTask<Response> BaseInvoke()
        {
            try
            {
                var resultTask = InvokeInner();
                if (resultTask.IsCompleted)
                {
                    return new ValueTask<Response>(Response.FromResult(resultTask.Result));
                }

                return CompleteInvokeAsync(resultTask);
            }
            catch (Exception exception)
            {
                return new ValueTask<Response>(Response.FromException(exception));
            }
        }

        private static async ValueTask<Response> CompleteInvokeAsync(ValueTask<TResult> resultTask)
        {
            try
            {
                var result = await resultTask;
                return Response.FromResult(result);
            }
            catch (Exception exception)
            {
                return Response.FromException(exception);
            }
        }

        // Generated
        protected abstract ValueTask<TResult> InvokeInner();
    }

    [GenerateSerializer]
    public abstract class TransactionTaskRequest<TResult> : TransactionRequestBase
    {
        [GeneratedActivatorConstructor]
        protected TransactionTaskRequest(Serializer<OrleansTransactionAbortedException> exceptionSerializer, IServiceProvider serviceProvider) : base(exceptionSerializer, serviceProvider)
        {
        }

        protected override ValueTask<Response> BaseInvoke()
        {
            try
            {
                var resultTask = InvokeInner();
                var status = resultTask.Status;
                if (resultTask.IsCompleted)
                {
                    return new ValueTask<Response>(Response.FromResult(resultTask.GetAwaiter().GetResult()));
                }

                return CompleteInvokeAsync(resultTask);
            }
            catch (Exception exception)
            {
                return new ValueTask<Response>(Response.FromException(exception));
            }
        }

        private static async ValueTask<Response> CompleteInvokeAsync(Task<TResult> resultTask)
        {
            try
            {
                var result = await resultTask;
                return Response.FromResult(result);
            }
            catch (Exception exception)
            {
                return Response.FromException(exception);
            }
        }

        // Generated
        protected abstract Task<TResult> InvokeInner();
    }

    [GenerateSerializer]
    public abstract class TransactionTaskRequest : TransactionRequestBase
    {
        [GeneratedActivatorConstructor]
        protected TransactionTaskRequest(Serializer<OrleansTransactionAbortedException> exceptionSerializer, IServiceProvider serviceProvider) : base(exceptionSerializer, serviceProvider)
        {
        }

        protected override ValueTask<Response> BaseInvoke()
        {
            try
            {
                var resultTask = InvokeInner();
                var status = resultTask.Status;
                if (resultTask.IsCompleted)
                {
                    resultTask.GetAwaiter().GetResult();
                    return new ValueTask<Response>(Response.Completed);
                }

                return CompleteInvokeAsync(resultTask);
            }
            catch (Exception exception)
            {
                return new ValueTask<Response>(Response.FromException(exception));
            }
        }

        private static async ValueTask<Response> CompleteInvokeAsync(Task resultTask)
        {
            try
            {
                await resultTask;
                return Response.Completed;
            }
            catch (Exception exception)
            {
                return Response.FromException(exception);
            }
        }

        // Generated
        protected abstract Task InvokeInner();
    }
}