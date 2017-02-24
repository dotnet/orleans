using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace TesterInternal
{
    [Serializable]
    public class GrainStateContainingGrainReferences
    {
        public IAddressable Grain { get; set; }
        public List<IAddressable> GrainList { get; set; }
        public Dictionary<string, IAddressable> GrainDict { get; set; }

        public GrainStateContainingGrainReferences()
        {
            GrainList = new List<IAddressable>();
            GrainDict = new Dictionary<string, IAddressable>();
        }
    }
}
