using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using System.Threading;
using UnitTests.GrainInterfaces;

namespace UnitTestGrains
{
    //public interface ISimpleGrain : IOrleansService
    //{
    //    void SetA(int a);
    //    void SetB(int b);
    //    int GetAxB();
    //    int GetAxB(int a, int b);
    //    int GetA();
    //    void IncrementA();
    //   // void LongMethod(int waitTime);
    //}

    public interface ISimpleGrain_Async : IGrain
    { 
        Task SetA_Async(int a);
        Task SetB_Async(int b);
        Task<int> GetAxB_Async();
        Task<int> GetAxB_Async(int a, int b);
        Task<int> GetA_Async();
        Task IncrementA_Async();
        //Task LongMethod_Async(int waitTime);
    }

    public interface ISimpleGrainWithAsyncMethods : ISimpleGrain_Async
    {
        Task<int> GetX();
        Task SetX(int x);
    }
}
