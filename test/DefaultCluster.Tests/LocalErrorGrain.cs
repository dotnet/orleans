using System;
using System.Threading;
using System.Threading.Tasks;

namespace DefaultCluster.Tests
{
    internal class LocalErrorGrain
    {
        int m_a = 0;
        int m_b = 0;

        public LocalErrorGrain() { }

        public Task SetA(int a)
        {
            m_a = a;
            return Task.CompletedTask;
        }

        public Task SetB(int b)
        {
            m_b = b;
            return Task.CompletedTask;
        }

        public Task<Int32> GetAxB()
        {
            return Task.FromResult(m_a * m_b);
        }

        public async Task<Int32> GetAxBError()
        {
            await Task.CompletedTask;
            throw new Exception("GetAxBError-Exception");
        }

        public Task LongMethod(int waitTime)
        {
            Thread.Sleep(waitTime);
            return Task.CompletedTask;
        }

        public async Task LongMethodWithError(int waitTime)
        {
            Thread.Sleep(waitTime);
            await Task.CompletedTask;
            throw new Exception("LongMethodWithError(" + waitTime + ")");
        }
    }
}