using System;
using Orleans.Runtime;

namespace Orleans.GrainDirectory
{
    [Serializable]
    internal struct AddressAndTag
    {
        public ActivationAddress Address;
        public int VersionTag;
    }
}