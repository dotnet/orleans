﻿using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    /// <summary>
    /// A simple grain that allows to set two agruments and then multiply them.
    /// </summary>
    public class AsyncSimpleGrain : SimpleGrain, ISimpleGrainWithAsyncMethods
    {
        TaskCompletionSource<int> resolver;

        public async Task<int> GetAxB_Async()
        {
            await Task.Delay(1000); // just to delay resolution of the promise for testing purposes
            return await GetAxB();
        }

        public async Task<int> GetAxB_Async(int a, int b)
        {
            await Task.Delay(1000); // just to delay resolution of the promise for testing purposes
            return await GetAxB(a, b);
        }
        public async Task SetA_Async(int a)
        {
            await Task.Delay(1000); // just to delay resolution of the promise for testing purposes
            await SetA(a);
        }
        public async Task SetB_Async(int b)
        {
            await Task.Delay(1000); // just to delay resolution of the promise for testing purposes
            await SetB(b);
        }

        public async Task IncrementA_Async()
        {
            await Task.Delay(1000); // just to delay resolution of the promise for testing purposes
            await IncrementA();
        }

        public async Task<int> GetA_Async()
        {
            await Task.Delay(1000); // just to delay resolution of the promise for testing purposes
            return await GetA();
        }

        public Task<int> GetX()
        {
            resolver = new TaskCompletionSource<int>();
            return resolver.Task;
        }

        public Task SetX(int x)
        {
            resolver.SetResult(x);
            return Task.CompletedTask;
        }
    }
}
