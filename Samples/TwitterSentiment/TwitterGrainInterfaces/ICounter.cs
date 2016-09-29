using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;


namespace TwitterGrainInterfaces
{
    /// <summary>
    /// A grain to keep track of the total number of hashtag grain activations
    /// </summary>
    public interface ICounter : IGrainWithIntegerKey
    {
        Task IncrementCounter();
        Task ResetCounter();
        Task<int> GetTotalCounter();
    }
}
