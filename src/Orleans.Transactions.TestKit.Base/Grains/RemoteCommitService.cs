using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Defines remote commit outcomes used to test transactional commit participation.
    /// </summary>
    public interface IRemoteCommitService
    {
        /// <summary>
        /// Records a successful remote commit.
        /// </summary>
        /// <param name="transactionId">The committed transaction identifier.</param>
        /// <param name="data">The operation data.</param>
        /// <returns><see langword="true"/> to report a successful commit.</returns>
        Task<bool> Pass(Guid transactionId, string data);

        /// <summary>
        /// Records a rejected remote commit.
        /// </summary>
        /// <param name="transactionId">The rejected transaction identifier.</param>
        /// <param name="data">The operation data.</param>
        /// <returns><see langword="false"/> to report a failed commit.</returns>
        Task<bool> Fail(Guid transactionId, string data);

        /// <summary>
        /// Records a remote commit and then throws an exception.
        /// </summary>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="data">The operation data.</param>
        /// <returns>A task which always faults.</returns>
        Task<bool> Throw(Guid transactionId, string data);
    }

    // TODO : Replace with more complete service implementation which:
    // - can be called to verify that commit service receive Callme with proper args.
    // - can produce errors for fault senarios.
    /// <summary>
    /// Provides deterministic successful, failed, and faulted remote commit outcomes for transaction tests.
    /// </summary>
    public partial class RemoteCommitService : IRemoteCommitService
    {
        private readonly ILogger logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteCommitService"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        public RemoteCommitService(ILogger<RemoteCommitService> logger)
        {
            this.logger = logger;
        }

        /// <inheritdoc/>
        public async Task<bool> Pass(Guid transactionId, string data)
        {
            LogInformationTransactionPassed(this.logger, transactionId, data);
            await Task.Delay(30);
            return true;
        }

        /// <inheritdoc/>
        public async Task<bool> Fail(Guid transactionId, string data)
        {
            LogInformationTransactionFailed(this.logger, transactionId, data);
            await Task.Delay(30);
            return false;
        }

        /// <inheritdoc/>
        public async Task<bool> Throw(Guid transactionId, string data)
        {
            LogInformationTransactionThrew(this.logger, transactionId, data);
            await Task.Delay(30);
            throw new ApplicationException("Transaction {transactionId} Threw with data: {data}");
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Transaction {TransactionId} Passed with data: {Data}"
        )]
        private static partial void LogInformationTransactionPassed(ILogger logger, Guid transactionId, string data);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Transaction {TransactionId} Failed with data: {Data}"
        )]
        private static partial void LogInformationTransactionFailed(ILogger logger, Guid transactionId, string data);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Transaction {TransactionId} Threw with data: {Data}"
        )]
        private static partial void LogInformationTransactionThrew(ILogger logger, Guid transactionId, string data);
    }

    /// <summary>
    /// Represents a remote commit operation which reports success.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class PassOperation : ITransactionCommitOperation<IRemoteCommitService>
    {
        /// <summary>
        /// Gets or sets the data supplied to the remote commit service.
        /// </summary>
        [Id(0)]
        public string Data { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="PassOperation"/> class.
        /// </summary>
        /// <param name="data">The data supplied to the remote commit service.</param>
        public PassOperation(string data)
        {
            this.Data = data;
        }

        /// <summary>
        /// Invokes the successful remote commit operation.
        /// </summary>
        /// <param name="transactionId">The committed transaction identifier.</param>
        /// <param name="service">The remote commit service.</param>
        /// <returns><see langword="true"/> when the remote commit succeeds.</returns>
        public async Task<bool> Commit(Guid transactionId, IRemoteCommitService service)
        {
            return await service.Pass(transactionId, this.Data);
        }
    }

    /// <summary>
    /// Represents a remote commit operation which reports failure.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class FailOperation : ITransactionCommitOperation<IRemoteCommitService>
    {
        /// <summary>
        /// Gets or sets the data supplied to the remote commit service.
        /// </summary>
        [Id(0)]
        public string Data { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="FailOperation"/> class.
        /// </summary>
        /// <param name="data">The data supplied to the remote commit service.</param>
        public FailOperation(string data)
        {
            this.Data = data;
        }

        /// <summary>
        /// Invokes the failed remote commit operation.
        /// </summary>
        /// <param name="transactionId">The rejected transaction identifier.</param>
        /// <param name="service">The remote commit service.</param>
        /// <returns><see langword="false"/> to reject the remote commit.</returns>
        public async Task<bool> Commit(Guid transactionId, IRemoteCommitService service)
        {
            return await service.Fail(transactionId, this.Data);
        }
    }

    /// <summary>
    /// Represents a remote commit operation which throws an exception.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class ThrowOperation : ITransactionCommitOperation<IRemoteCommitService>
    {
        /// <summary>
        /// Gets or sets the data supplied to the remote commit service.
        /// </summary>
        [Id(0)]
        public string Data { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ThrowOperation"/> class.
        /// </summary>
        /// <param name="data">The data supplied to the remote commit service.</param>
        public ThrowOperation(string data)
        {
            this.Data = data;
        }

        /// <summary>
        /// Invokes the faulting remote commit operation.
        /// </summary>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="service">The remote commit service.</param>
        /// <returns>A task which always faults.</returns>
        public async Task<bool> Commit(Guid transactionId, IRemoteCommitService service)
        {
            return await service.Throw(transactionId, this.Data);
        }
    }
}
