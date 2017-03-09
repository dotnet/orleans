using System;
using Orleans.GrainDirectory;

namespace Orleans.Runtime
{
    internal interface IActivationInfo
    {
        SiloAddress SiloAddress { get; }
        DateTime TimeCreated { get; }
        GrainDirectoryEntryStatus RegistrationStatus { get; set; }
        bool OkToRemove(UnregistrationCause cause);
    }
}