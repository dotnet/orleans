using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Transactions.DeadlockDetection;

namespace Orleans.Transactions.TestKit.Base.Grains
{
    public interface IDeadlockTester : IGrainWithIntegerKey
    {
    }
}