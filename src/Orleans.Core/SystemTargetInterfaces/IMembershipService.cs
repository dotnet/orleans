using System.Threading.Tasks;


namespace Orleans.Runtime
{
    internal interface IMembershipService : ISystemTarget
    {
        /// <summary>
        /// Receive notifications about silo status events. 
        /// </summary>
        /// <param name="updatedSilo">Silo to update about</param>
        /// <param name="status">Status of the silo</param>
        /// <returns></returns>
        Task SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status);

        /// <summary>
        /// Ping request from another silo that probes the liveness of the recipient silo.
        /// </summary>
        /// <param name="pingNumber">A unique sequence number for ping message, to facilitate testijng and debugging.</param>
        Task Ping(int pingNumber);
    }
}
