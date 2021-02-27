namespace Orleans.Runtime
{
    /// <summary>
    /// Possible statuses of a silo.
    /// </summary>
    [GenerateSerializer]
    public enum SiloStatus
    {
        None = 0,
        /// <summary>
        /// This silo was just created, but not started yet.
        /// </summary>
        Created = 1,
        /// <summary>
        /// This silo has just started, but not ready yet. It is attempting to join the cluster.
        /// </summary>
        Joining = 2,         
        /// <summary>
        /// This silo is alive and functional.
        /// </summary>
        Active = 3,
        /// <summary>
        /// This silo is shutting itself down.
        /// </summary>
        ShuttingDown = 4,    
        /// <summary>
        /// This silo is stopping itself down.
        /// </summary>
        Stopping = 5,
        /// <summary>
        /// This silo is deactivated/considered to be dead.
        /// </summary>
        Dead = 6
    }

    public static class SiloStatusExtensions
    {
        /// <summary>
        /// Return true if this silo is currently terminating: ShuttingDown, Stopping or Dead.
        /// </summary>
        public static bool IsTerminating(this SiloStatus siloStatus)
        {
            return siloStatus == SiloStatus.ShuttingDown || siloStatus == SiloStatus.Stopping || siloStatus == SiloStatus.Dead;
        }
    }
}
