using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    /// <summary>
    /// 
    /// </summary>
    internal interface IGrainCoroutine : ISystemTarget, IAddressable
    {
        Task Start();
        Task Continue(IGrainCoroutine prev);
        Task Stop();
        Task ExcuteTask { get; }
    }

    /// <summary>
    /// 
    /// </summary>
    internal class GrainCoroutine : Grain, IGrainCoroutine
    {
        public Task ExcuteTask => throw new NotImplementedException();

        public Task Continue(IGrainCoroutine prev)
        {
            return prev.ExcuteTask;
        }

        public Task Start()
        {
            throw new NotImplementedException();
        }

        public Task Stop()
        {
            throw new NotImplementedException();
        }
    }
}
