using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
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
        /// <summary>
        /// Initializes a new instance of the <see cref="TransactionAttribute"/> class.
        /// </summary>
        /// <param name="requirement">The transaction behavior required by the attributed method.</param>
        public TransactionAttribute(TransactionOption requirement)
        {
            Requirement = requirement;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TransactionAttribute"/> class.
        /// </summary>
        /// <param name="alias">An alias for the transaction behavior required by the attributed method.</param>
        public TransactionAttribute(TransactionOptionAlias alias)
        {
            Requirement = (TransactionOption)(int)alias;
        }

        /// <summary>
        /// Gets the transaction behavior required by the attributed method.
        /// </summary>
        public TransactionOption Requirement { get; }

        /// <summary>
        /// Gets or sets a value indicating whether the transaction permits read operations only.
        /// </summary>
        [Obsolete("Use [ReadOnly] attribute instead.")]
        public bool ReadOnly { get; set; }
    }

    /// <summary>
    /// The UseExclusiveLock attribute is used to mark transactional methods that should acquire
    /// exclusive locks even for read operations. This prevents frequent lock upgrade conflicts under high contention.
    /// </summary>
    /// <example>
    /// <code>
    /// [UseExclusiveLock]
    /// [Transaction(TransactionOption.CreateOrJoin)]
    /// Task&lt;uint&gt; GetBalance();
    /// </code>
    /// </example>
    [InvokableCustomInitializer("SetExclusiveLock", true)]
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class UseExclusiveLockAttribute : Attribute
    {
    }

    /// <summary>
    /// Specifies how a method call or delegate interacts with an ambient transaction.
    /// </summary>
    public enum TransactionOption
    {
        /// <summary>
        /// Executes grain calls without propagating an ambient transaction. Delegate execution rejects an existing ambient transaction.
        /// </summary>
        Suppress,

        /// <summary>
        /// Joins the ambient transaction when one exists; otherwise, creates a transaction.
        /// </summary>
        CreateOrJoin,

        /// <summary>
        /// Creates a transaction, independently of any ambient transaction.
        /// </summary>
        Create,

        /// <summary>
        /// Joins the ambient transaction and requires one to exist.
        /// </summary>
        Join,

        /// <summary>
        /// Propagates the ambient transaction when one exists and executes without a transaction otherwise.
        /// </summary>
        Supported,

        /// <summary>
        /// Executes without a transaction and rejects calls made within an ambient transaction.
        /// </summary>
        NotAllowed
    }

    /// <summary>
    /// Provides compatibility aliases for <see cref="TransactionOption"/> values.
    /// </summary>
    public enum TransactionOptionAlias
    {
<<<<<<< HEAD
        /// <summary>
        /// Maps to <see cref="TransactionOption.Supported"/>.
        /// </summary>
        Suppress = TransactionOption.Supported,

        /// <summary>
        /// Maps to <see cref="TransactionOption.CreateOrJoin"/>.
        /// </summary>
        Required = TransactionOption.CreateOrJoin,

        /// <summary>
        /// Maps to <see cref="TransactionOption.Create"/>.
        /// </summary>
        RequiresNew = TransactionOption.Create,

        /// <summary>
        /// Maps to <see cref="TransactionOption.Join"/>.
        /// </summary>
        Mandatory = TransactionOption.Join,

        /// <summary>
        /// Maps to <see cref="TransactionOption.NotAllowed"/>.
        /// </summary>
        Never = TransactionOption.NotAllowed,
||||||| parent of 82a763ec4 (style: format solution whitespace)
        Suppress     = TransactionOption.Supported,
        Required     = TransactionOption.CreateOrJoin,
        RequiresNew  = TransactionOption.Create,
        Mandatory    = TransactionOption.Join,
        Never        = TransactionOption.NotAllowed,
=======
        Suppress = TransactionOption.Supported,
        Required = TransactionOption.CreateOrJoin,
        RequiresNew = TransactionOption.Create,
        Mandatory = TransactionOption.Join,
        Never = TransactionOption.NotAllowed,
>>>>>>> 82a763ec4 (style: format solution whitespace)
    }

    /// <summary>
    /// Provides transaction propagation and resolution for generated grain request invokers.
    /// </summary>
    [GenerateSerializer]
    public abstract class TransactionRequestBase : RequestBase, IOutgoingGrainCallFilter, IOnDeserialized
    {
        [NonSerialized]
        private Serializer<OrleansTransactionAbortedException> _serializer;

        [NonSerialized]
        private ITransactionAgent? _transactionAgent;

        private ITransactionAgent TransactionAgent => _transactionAgent ?? throw new OrleansTransactionsDisabledException();

        /// <summary>
        /// Gets or sets the transaction behavior for the request.
        /// </summary>
        [Id(0)]
        public TransactionOption TransactionOption { get; set; }

        /// <summary>
        /// Gets or sets the transaction information propagated with the request.
        /// </summary>
        [Id(1)]
        public TransactionInfo? TransactionInfo { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether transactional state reads acquire exclusive locks.
        /// </summary>
        [Id(2)]
        public bool UseExclusiveLock { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="TransactionRequestBase"/> class.
        /// </summary>
        /// <param name="exceptionSerializer">The serializer for transaction abort exceptions.</param>
        /// <param name="serviceProvider">The service provider used to resolve transaction services.</param>
        [GeneratedActivatorConstructor]
        protected TransactionRequestBase(Serializer<OrleansTransactionAbortedException> exceptionSerializer, IServiceProvider serviceProvider)
        {
            _serializer = exceptionSerializer;

            // May be null, eg on an external client. We will throw if it's null at the time of invocation.
            _transactionAgent = serviceProvider.GetService<ITransactionAgent>();
        }

        /// <summary>
        /// Gets a value indicating whether the request executes outside the ambient transaction.
        /// </summary>
        public bool IsAmbientTransactionSuppressed => TransactionOption switch
        {
            TransactionOption.Create => true,
            TransactionOption.Suppress => true,
            _ => false
        };

        /// <summary>
        /// Gets a value indicating whether the request requires a transaction context.
        /// </summary>
        public bool IsTransactionRequired => TransactionOption switch
        {
            TransactionOption.Create => true,
            TransactionOption.CreateOrJoin => true,
            TransactionOption.Join => true,
            _ => false
        };

        /// <summary>
        /// Sets the transaction behavior using a compatibility alias.
        /// </summary>
        /// <param name="txOption">The transaction option alias.</param>
        protected void SetTransactionOptions(TransactionOptionAlias txOption) => SetTransactionOptions((TransactionOption)txOption);

        /// <summary>
        /// Sets the transaction behavior for the request.
        /// </summary>
        /// <param name="txOption">The transaction option.</param>
        protected void SetTransactionOptions(TransactionOption txOption)
        {
            this.TransactionOption = txOption;
        }

        /// <summary>
        /// Sets whether transactional state reads acquire exclusive locks.
        /// </summary>
        /// <param name="value">
        /// <see langword="true"/> to acquire exclusive locks for reads; otherwise, <see langword="false"/>.
        /// </param>
        protected void SetExclusiveLock(bool value)
        {
            this.UseExclusiveLock = value;
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
                if (context.Response is TransactionResponse txResponse)
                {
                    var returnedTransactionInfo = txResponse.TransactionInfo;

                    if (transactionInfo is { } && returnedTransactionInfo is { })
                    {
                        transactionInfo.Join(returnedTransactionInfo);
                    }

                    if (txResponse.GetException() is { } exception)
                    {
                        ExceptionDispatchInfo.Throw(exception);
                    }
                }
            }
        }

        private TransactionInfo? SetTransactionInfo()
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

        /// <inheritdoc/>
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
                    var isReadOnly = this.Options.HasFlag(InvokeMethodOptions.ReadOnly);
                    transactionInfo = await TransactionAgent.StartTransaction(isReadOnly, transactionTimeout);
                    startedNewTransaction = true;
                }

                // Apply this flag if requested by Attribute
                if (this.UseExclusiveLock && transactionInfo != null)
                {
                    transactionInfo.UseExclusiveLock = true;
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

                OrleansTransactionException? transactionException = transactionInfo.MustAbort(_serializer);

                // This request started the transaction, so we try to commit before returning,
                // or if it must abort, tell participants that it aborted
                if (startedNewTransaction)
                {
                    if (transactionException is not null || transactionInfo.TryToCommit is false)
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

        /// <summary>
        /// Invokes the generated request implementation.
        /// </summary>
        /// <returns>A response representing the invocation result.</returns>
        protected abstract ValueTask<Response> BaseInvoke();

        /// <inheritdoc/>
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

    /// <summary>
    /// Wraps a response together with the transaction information produced by an invocation.
    /// </summary>
    [GenerateSerializer]
    public sealed class TransactionResponse : Response
    {
        [Id(0)]
        private Response _response = null!;

        /// <summary>
        /// Gets or sets the transaction information returned by the invocation.
        /// </summary>
        [Id(1)]
        public TransactionInfo? TransactionInfo { get; set; }

        /// <summary>
        /// Creates a transaction response which wraps an invocation response.
        /// </summary>
        /// <param name="response">The invocation response.</param>
        /// <param name="transactionInfo">The transaction information to return with the response.</param>
        /// <returns>The transaction response.</returns>
        public static TransactionResponse Create(Response response, TransactionInfo transactionInfo)
        {
            return new TransactionResponse
            {
                _response = response,
                TransactionInfo = transactionInfo
            };
        }

        /// <summary>
        /// Gets the wrapped invocation response.
        /// </summary>
        public Response InnerResponse => _response;

        /// <summary>
        /// Gets or sets the wrapped response result.
        /// </summary>
        public override object? Result
        {
            get
            {
                if (_response.Exception is { } exception)
                {
                    ExceptionDispatchInfo.Capture(exception).Throw();
                }

                return _response.Result;
            }

            set => _response.Result = value;
        }

        /// <summary>
        /// Gets <see langword="null"/> so that the wrapper can be delivered as a successful response, or sets the
        /// exception on the wrapped response.
        /// </summary>
        public override Exception? Exception
        {
            get
            {
                // Suppress any exception here, allowing ResponseCompletionSource to complete with a Response instead of an exception.
                // This gives TransactionRequestBase a chance to inspect this instance and retrieve the TransactionInfo property first.
                // After, it will use GetException to get and throw the exeption.
                return null;
            }

            set => _response.Exception = value;
        }

        /// <summary>
        /// Gets the exception from the wrapped response.
        /// </summary>
        /// <returns>The wrapped exception, or <see langword="null"/> if the invocation completed successfully.</returns>
        public Exception? GetException() => _response.Exception;

        /// <inheritdoc/>
        public override string ToString() => _response?.ToString() ?? "[null]";

        /// <inheritdoc/>
        public override void Dispose()
        {
            TransactionInfo = null;
            _response.Dispose();
        }

        /// <inheritdoc/>
        [return: MaybeNull]
        public override T GetResult<T>() => _response.GetResult<T>();
    }

    /// <summary>
    /// Base type for generated transactional request invokers which return a <see cref="ValueTask"/>.
    /// </summary>
    [SerializerTransparent]
    public abstract class TransactionRequest : TransactionRequestBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TransactionRequest"/> class.
        /// </summary>
        /// <param name="exceptionSerializer">The serializer for transaction abort exceptions.</param>
        /// <param name="serviceProvider">The service provider used to resolve transaction services.</param>
        protected TransactionRequest(Serializer<OrleansTransactionAbortedException> exceptionSerializer, IServiceProvider serviceProvider) : base(exceptionSerializer, serviceProvider)
        {
        }

        /// <inheritdoc/>
        protected sealed override ValueTask<Response> BaseInvoke()
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
        /// <summary>
        /// Invokes the generated method implementation.
        /// </summary>
        /// <returns>A task representing the invocation.</returns>
        protected abstract ValueTask InvokeInner();
    }

    /// <summary>
    /// Base type for generated transactional request invokers which return a <see cref="ValueTask{TResult}"/>.
    /// </summary>
    /// <typeparam name="TResult">The invocation result type.</typeparam>
    [SerializerTransparent]
    public abstract class TransactionRequest<TResult> : TransactionRequestBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TransactionRequest{TResult}"/> class.
        /// </summary>
        /// <param name="exceptionSerializer">The serializer for transaction abort exceptions.</param>
        /// <param name="serviceProvider">The service provider used to resolve transaction services.</param>
        protected TransactionRequest(Serializer<OrleansTransactionAbortedException> exceptionSerializer, IServiceProvider serviceProvider) : base(exceptionSerializer, serviceProvider)
        {
        }

        /// <inheritdoc/>
        protected sealed override ValueTask<Response> BaseInvoke()
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
        /// <summary>
        /// Invokes the generated method implementation.
        /// </summary>
        /// <returns>A task containing the invocation result.</returns>
        protected abstract ValueTask<TResult> InvokeInner();
    }

    /// <summary>
    /// Base type for generated transactional request invokers which return a <see cref="Task{TResult}"/>.
    /// </summary>
    /// <typeparam name="TResult">The invocation result type.</typeparam>
    [SerializerTransparent]
    public abstract class TransactionTaskRequest<TResult> : TransactionRequestBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TransactionTaskRequest{TResult}"/> class.
        /// </summary>
        /// <param name="exceptionSerializer">The serializer for transaction abort exceptions.</param>
        /// <param name="serviceProvider">The service provider used to resolve transaction services.</param>
        protected TransactionTaskRequest(Serializer<OrleansTransactionAbortedException> exceptionSerializer, IServiceProvider serviceProvider) : base(exceptionSerializer, serviceProvider)
        {
        }

        /// <inheritdoc/>
        protected sealed override ValueTask<Response> BaseInvoke()
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
        /// <summary>
        /// Invokes the generated method implementation.
        /// </summary>
        /// <returns>A task containing the invocation result.</returns>
        protected abstract Task<TResult> InvokeInner();
    }

    /// <summary>
    /// Base type for generated transactional request invokers which return a <see cref="Task"/>.
    /// </summary>
    [SerializerTransparent]
    public abstract class TransactionTaskRequest : TransactionRequestBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TransactionTaskRequest"/> class.
        /// </summary>
        /// <param name="exceptionSerializer">The serializer for transaction abort exceptions.</param>
        /// <param name="serviceProvider">The service provider used to resolve transaction services.</param>
        protected TransactionTaskRequest(Serializer<OrleansTransactionAbortedException> exceptionSerializer, IServiceProvider serviceProvider) : base(exceptionSerializer, serviceProvider)
        {
        }

        /// <inheritdoc/>
        protected sealed override ValueTask<Response> BaseInvoke()
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
        /// <summary>
        /// Invokes the generated method implementation.
        /// </summary>
        /// <returns>A task representing the invocation.</returns>
        protected abstract Task InvokeInner();
    }
}
