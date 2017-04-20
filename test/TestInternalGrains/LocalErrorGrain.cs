using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans;

namespace UnitTests
{
    internal class LocalErrorGrain
    {
        int m_a = 0;
        int m_b = 0;

        public LocalErrorGrain() { }

        public Task SetA(int a)
        {
            m_a = a;
            return TaskDone.Done;
        }

        public Task SetB(int b)
        {
            m_b = b;
            return TaskDone.Done;
        }

        public Task<Int32> GetAxB()
        {
            return Task.FromResult(m_a * m_b);
        }

        public async Task<Int32> GetAxBError()
        {
            await TaskDone.Done;
            throw new Exception("GetAxBError-Exception");
        }

        public Task LongMethod(int waitTime)
        {
            Thread.Sleep(waitTime);
            return TaskDone.Done;
        }

        public async Task LongMethodWithError(int waitTime)
        {
            Thread.Sleep(waitTime);
            await TaskDone.Done;
            throw new Exception("LongMethodWithError(" + waitTime + ")");
        }
    }
}