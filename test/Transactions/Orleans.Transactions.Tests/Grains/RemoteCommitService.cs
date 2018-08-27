using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.Tests
{
    public interface IRemoteCommitService
    {
        Task<bool> Callme(Guid transactionId, string data);
    }

    // TODO : Replace with more complete service implementation which:
    // - can be called to verify that commit service recieve Callme with proper args.
    // - can produce errors for fault senarios.
    public class RemoteCommitService : IRemoteCommitService
    {
        ILogger logger;

        public RemoteCommitService(ILogger<RemoteCommitService> logger)
        {
            this.logger = logger;
        }
        public Task<bool> Callme(Guid transactionId, string data)
        {
            this.logger.LogInformation($"Transaction {transactionId} committed with data: {data}");
            return Task.FromResult(true);
        }
    }

    [Serializable]
    public class RemoteCommitServiceOperation : ITransactionCommitOperation<IRemoteCommitService>
    {
        public string Data { get; set; }

        public RemoteCommitServiceOperation(string data)
        {
            this.Data = data;
        }

        public async Task<bool> Commit(Guid transactionId, IRemoteCommitService service)
        {
            try
            {
                return await service.Callme(transactionId, this.Data);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
