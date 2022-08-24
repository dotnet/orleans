using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.TestKit
{
    public interface IRemoteCommitService
    {
        Task<bool> Pass(Guid transactionId, string data);
        Task<bool> Fail(Guid transactionId, string data);
        Task<bool> Throw(Guid transactionId, string data);
    }

    // TODO : Replace with more complete service implementation which:
    // - can be called to verify that commit service receive Callme with proper args.
    // - can produce errors for fault senarios.
    public class RemoteCommitService : IRemoteCommitService
    {
        ILogger logger;

        public RemoteCommitService(ILogger<RemoteCommitService> logger)
        {
            this.logger = logger;
        }

        public async Task<bool> Pass(Guid transactionId, string data)
        {
            this.logger.LogInformation("Transaction {TransactionId} Passed with data: {Data}", transactionId, data);
            await Task.Delay(30);
            return true;
        }

        public async Task<bool> Fail(Guid transactionId, string data)
        {
            this.logger.LogInformation("Transaction {TransactionId} Failed with data: {Data}", transactionId, data);
            await Task.Delay(30);
            return false;
        }

        public async Task<bool> Throw(Guid transactionId, string data)
        {
            this.logger.LogInformation("Transaction {TransactionId} Threw with data: {Data}", transactionId, data);
            await Task.Delay(30);
            throw new ApplicationException("Transaction {transactionId} Threw with data: {data}");
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class PassOperation : ITransactionCommitOperation<IRemoteCommitService>
    {
        [Id(0)]
        public string Data { get; set; }

        public PassOperation(string data)
        {
            this.Data = data;
        }

        public async Task<bool> Commit(Guid transactionId, IRemoteCommitService service)
        {
            return await service.Pass(transactionId, this.Data);
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class FailOperation : ITransactionCommitOperation<IRemoteCommitService>
    {
        [Id(0)]
        public string Data { get; set; }

        public FailOperation(string data)
        {
            this.Data = data;
        }

        public async Task<bool> Commit(Guid transactionId, IRemoteCommitService service)
        {
            return await service.Fail(transactionId, this.Data);
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class ThrowOperation : ITransactionCommitOperation<IRemoteCommitService>
    {
        [Id(0)]
        public string Data { get; set; }

        public ThrowOperation(string data)
        {
            this.Data = data;
        }

        public async Task<bool> Commit(Guid transactionId, IRemoteCommitService service)
        {
            return await service.Throw(transactionId, this.Data);
        }
    }
}
