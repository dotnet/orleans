using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;

namespace UnitTests.GrainInterfaces
{
    public interface IReentrantGrain : IGrainWithIntegerKey
    {
        Task<string> One();

        Task<string> Two();

        Task SetSelf(IReentrantGrain self);
    }

    public interface INonReentrantGrain : IGrainWithIntegerKey
    {
        Task<string> One();

        Task<string> Two();

        Task SetSelf(INonReentrantGrain self);
    }

    [Unordered]
    public interface IUnorderedNonReentrantGrain : IGrainWithIntegerKey
    {
        Task<string> One();

        Task<string> Two();

        Task SetSelf(IUnorderedNonReentrantGrain self);
    }

    public interface IReentrantSelfManagedGrain : IGrainWithIntegerKey
    {
        Task<int> GetCounter();

        Task Ping(int seconds);

        Task SetDestination(long id);
    }

    public interface INonReentrantSelfManagedGrain : IGrainWithIntegerKey
    {
        Task<int> GetCounter();

        Task Ping(int seconds);

        Task SetDestination(long id);
    }

    public interface IReentrantTaskGrain : IGrainWithIntegerKey
    {
        Task SetDestination(long id);
        Task Ping(TimeSpan wait);
        Task<int> GetCounter();
    }

    public interface INonReentrantTaskGrain : IGrainWithIntegerKey
    {
        Task SetDestination(long id);
        Task Ping(TimeSpan wait);
        Task<int> GetCounter();
    }

    public interface IFanOutGrain : IGrainWithIntegerKey
    {
        Task FanOutReentrant(int offset, int num);
        Task FanOutNonReentrant(int offset, int num);
        Task FanOutReentrant_Chain(int offset, int num);
        Task FanOutNonReentrant_Chain(int offset, int num);
    }

    public interface IFanOutACGrain : IGrainWithIntegerKey
    {
        Task FanOutACReentrant(int offset, int num);
        Task FanOutACNonReentrant(int offset, int num);
        Task FanOutACReentrant_Chain(int offset, int num);
        Task FanOutACNonReentrant_Chain(int offset, int num);
    }

    public interface IReentrantTestSupportGrain : IGrainWithIntegerKey
    {
        Task<bool> IsReentrant(string fullTypeName);
    }
}
