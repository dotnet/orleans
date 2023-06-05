using Orleans.Concurrency;

namespace UnitTests.GrainInterfaces
{
    public interface IDeadlockNonReentrantGrain : IGrainWithIntegerKey
    {
        Task CallNext_1(List<(long GrainId, bool Blocking)> callChain, int currCallIndex);
        Task CallNext_2(List<(long GrainId, bool Blocking)> callChain, int currCallIndex);
    }

    public interface IDeadlockReentrantGrain : IGrainWithIntegerKey
    {
        Task CallNext_1(List<(long GrainId, bool Blocking)> callChain, int currCallIndex);
        Task CallNext_2(List<(long GrainId, bool Blocking)> callChain, int currCallIndex);
    }

    public interface ICallChainObserver : IGrainObserver
    {
        Task OnEnter(string grain, int callIndex);
        Task OnExit(string grain, int callIndex);
    }

    public interface ICallChainReentrancyGrain : IGrainWithStringKey
    {
        Task CallChain(ICallChainObserver observer, List<(string TargetGrain, ReentrancyCallType CallType)> callChain, int callIndex);

        [AlwaysInterleave]
        Task UnblockWaiters();
    }

    [GenerateSerializer]
    public enum ReentrancyCallType
    {
        Regular,
        AllowCallChainReentrancy,
        SuppressCallChainReentrancy,
    }
}

