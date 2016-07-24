﻿using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;

namespace UnitTests.GrainInterfaces
{
    /// <summary>
    ///  A simple echo grain
    /// </summary>
    public interface IEchoGrain : IGrainWithGuidKey
    {
        Task<string> GetLastEcho();

        Task<string> Echo(string data);
        Task<string> EchoError(string data);
    }

    public interface IEchoTaskGrain : IGrainWithGuidKey
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

    public interface IBlockingEchoTaskGrain : IGrainWithIntegerKey
    {
        Task<int> GetMyId();

        Task<string> GetLastEcho();

        Task<string> Echo(string data);
        Task<string> CallMethodTask_Await(string data);
        Task<string> CallMethodAV_Await(string data);
        Task<string> CallMethodTask_Block(string data);
        Task<string> CallMethodAV_Block(string data);
    }

    public interface IReentrantBlockingEchoTaskGrain : IGrainWithIntegerKey
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
