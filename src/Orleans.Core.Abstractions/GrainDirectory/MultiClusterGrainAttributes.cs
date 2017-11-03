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

    /// <summary>
    /// This attribute indicates that instances of the marked grain class will have a single instance across all available clusters. Any requests in any clusters will be forwarded to the single activation instance.
    /// </summary>
    public class GlobalSingleInstanceAttribute : RegistrationAttribute
    {
        public GlobalSingleInstanceAttribute()
            : base(GlobalSingleInstanceRegistration.Singleton)
        {
        }
    }

    /// <summary>
    /// This attribute indicates that instances of the marked grain class
    /// will have an independent instance for each cluster with
    /// no coordination.
    /// </summary>
    public class OneInstancePerClusterAttribute : RegistrationAttribute
    {
        public OneInstancePerClusterAttribute()
            : base(ClusterLocalRegistration.Singleton)
        {
        }
    }
}