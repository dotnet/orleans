using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LoadTestGrainInterfaces;
using Orleans;

namespace LoadTestGrains
{
    public class GuidPoolGrain : Grain, IGuidPoolGrain
    {
        private Guid[] _guids;

        public Task<Guid[]> GetGuids(int count)
        {
            if (null == _guids)
            {
                if (count < 1)
                    throw new ArgumentException("count must be greater than 1");
                else
                {
                    Guid[] guids = new Guid[count];
                    for (int i = 0; i < guids.Length; ++i)
                        guids[i] = Guid.NewGuid();
                    _guids = guids;
                }
            }
            else if (_guids.Length != count)
                throw new ArgumentException("guid array length mismatch");

            return Task.FromResult(_guids);
        }
    }
}
