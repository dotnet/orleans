using Orleans;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace OrleansGrainInterfaces
{
    public interface IApproval<T> : IGrainWithGuidKey
    {
        Task<bool> Approve(T proposal);
        Task<bool> Reject(T proposal);
    }
}
