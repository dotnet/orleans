using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;

namespace UnitTestGrains
{
    public interface IReentrantGrain : IGrain
    {
        Task<string> One();

        Task<string> Two();

        Task SetSelf(IReentrantGrain self);
    }

    public interface INonReentrantGrain : IGrain
    {
        Task<string> One();

        Task<string> Two();

        Task SetSelf(INonReentrantGrain self);
    }

    [Unordered]
    public interface IUnorderedNonReentrantGrain : IGrain
    {
        Task<string> One();

        Task<string> Two();

        Task SetSelf(IUnorderedNonReentrantGrain self);
    }

    public interface IReentrantSelfManagedGrain : IGrain
    {
        Task<int> GetCounter();

        Task Ping(int seconds);

        Task SetDestination(long id);
    }

    public interface INonReentrantSelfManagedGrain : IGrain
    {
        Task<int> GetCounter();

        Task Ping(int seconds);

        Task SetDestination(long id);
    }

    public interface IReentrantTaskGrain : IGrain
    {
        Task SetDestination(long id);
        Task Ping(TimeSpan wait);
        Task<int> GetCounter();
    }

    public interface INonReentrantTaskGrain : IGrain
    {
        Task SetDestination(long id);
        Task Ping(TimeSpan wait);
        Task<int> GetCounter();
    }

    public interface IFanOutGrain : IGrain
    {
        Task FanOutReentrant(int offset, int num);
        Task FanOutNonReentrant(int offset, int num);
        Task FanOutReentrant_Chain(int offset, int num);
        Task FanOutNonReentrant_Chain(int offset, int num);
    }

    public interface IFanOutACGrain : IGrain
    {
        Task FanOutACReentrant(int offset, int num);
        Task FanOutACNonReentrant(int offset, int num);
        Task FanOutACReentrant_Chain(int offset, int num);
        Task FanOutACNonReentrant_Chain(int offset, int num);
    }

    public interface IReentrantTestSupportGrain : IGrain
    {
        Task<bool> IsReentrant(string fullTypeName);
    }
}
