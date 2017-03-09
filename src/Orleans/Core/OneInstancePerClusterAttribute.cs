using Orleans.GrainDirectory;

namespace Orleans.MultiCluster
{
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