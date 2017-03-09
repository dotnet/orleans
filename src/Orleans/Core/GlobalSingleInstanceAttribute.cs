using Orleans.GrainDirectory;

namespace Orleans.MultiCluster
{
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
}