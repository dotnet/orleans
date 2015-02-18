using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;


namespace Echo
{
    /// <summary>
    ///  A simple echo grain
    /// </summary>
    public interface IEchoGrain : IGrain
    {
        Task<string> GetLastEcho();

        Task<string> Echo(string data);
        Task<string> EchoError(string data);
    }

    public interface IEchoTaskGrain : IGrain
    {
        Task<int> GetMyIdAsync();

        Task<string> GetLastEchoAsync();

        Task<string> EchoAsync(string data);
        Task<string> EchoErrorAsync(string data);

        Task<int> BlockingCallTimeoutAsync(TimeSpan delay);

        Task PingAsync();
        Task PingLocalSiloAsync();
        Task PingRemoteSiloAsync(SiloAddress siloAddress);
        Task PingOtherSiloAsync();
        Task PingClusterMemberAsync();
    }

    public interface IBlockingEchoTaskGrain : IGrain
    {
        Task<int> GetMyId();

        Task<string> GetLastEcho();

        Task<string> Echo(string data);
        Task<string> CallMethodTask_Await(string data);
        Task<string> CallMethodAV_Await(string data);
        Task<string> CallMethodTask_Block(string data);
        Task<string> CallMethodAV_Block(string data);
    }

    public interface IReentrantBlockingEchoTaskGrain : IGrain
    {
        Task<int> GetMyId();

        Task<string> GetLastEcho();

        Task<string> Echo(string data);
        Task<string> CallMethodTask_Await(string data);
        Task<string> CallMethodAV_Await(string data);
        Task<string> CallMethodTask_Block(string data);
        Task<string> CallMethodAV_Block(string data);
    }
}
