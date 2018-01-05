using Orleans;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace OrleansGrains
{
    public class Approval<T> : Grain, OrleansGrainInterfaces.IApproval<T>
    {
        public async Task<bool> Approve(T proposal)
        {
            return await Task.FromResult(true);
        }

        public async Task<bool> Reject(T proposal)
        {
            return await Task.FromResult(false);
        }
    }
}
