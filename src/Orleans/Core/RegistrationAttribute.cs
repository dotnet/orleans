using System;
using Orleans.GrainDirectory;

namespace Orleans.MultiCluster
{
    /// <summary>
    /// base class for multi cluster registration strategies.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public abstract class RegistrationAttribute : Attribute
    {
        internal MultiClusterRegistrationStrategy RegistrationStrategy { get; private set; }

        internal RegistrationAttribute(MultiClusterRegistrationStrategy strategy)
        {
            this.RegistrationStrategy = strategy;
        }
    }
}