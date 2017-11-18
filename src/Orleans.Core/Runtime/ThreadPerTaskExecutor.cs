using System.Threading;

namespace Orleans.Runtime
{
    internal class ThreadPerTaskExecutor : IExecutor
    {
        private readonly string name;

        public ThreadPerTaskExecutor(string name)
        {
            this.name = name;
        }
        
        public void QueueWorkItem(WaitCallback callBack, object state = null)
        {
            new Thread(() => callBack.Invoke(state))
            {
                IsBackground = true,
                Name = name
            }.Start();
        }

        public int WorkQueueLength => 0;
    }
}