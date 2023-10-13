namespace DefaultCluster.Tests
{
    internal class LocalErrorGrain
    {
        private int m_a = 0;
        private int m_b = 0;

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

        public Task<int> GetAxB()
        {
            return Task.FromResult(m_a * m_b);
        }

        public async Task<int> GetAxBError()
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