namespace Orleans.Runtime
{
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