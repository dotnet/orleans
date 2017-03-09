using System;
using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.GrainDirectory
{
    [Serializable]
    internal struct AddressesAndTag 
    {
        public List<ActivationAddress> Addresses;
        public int VersionTag;
    }
}