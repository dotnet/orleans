using System;
using System.Threading.Tasks;

namespace Orleans.Runtime.MultiClusterNetwork
{
    /// <summary>
    /// Interface for gossip channel.
    /// 
    /// A gossip channel stores multicluster data (configuration, gateways) and exchanges
    /// this data with silos using a gossip-style communication, offering
    /// two different methods (Publish or Synchronize).
    /// </summary>
    public interface IGossipChannel
    {
        /// <summary>
        /// Initialize the channel with given configuration.
        /// </summary>
        /// <param name="serviceId"></param>
        /// <param name="connectionString"></param>
        /// <returns></returns>
        Task Initialize(Guid serviceId, string connectionString);

        /// <summary>
        /// A name for the channel.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// One-way small-scale gossip. 
        /// Used to update small amounts of data (e.g. multicluster configuration, single gateway status) in the channel.
        /// The passed-in data is stored only if it is newer than the already stored data.
        /// </summary>
        /// <param name="data">The data to update</param>
        /// <returns></returns>
        Task Publish(MultiClusterData data);

         /// <summary>
        /// Two-way bulk gossip.
        /// - any passed-in information that is newer than stored information is stored.
        /// - any stored information that is newer than passed-in information is returned.
        /// </summary>
        /// <param name="gossipdata">The gossip data to compare to the current contents, and store if newer, or not there</param>
        /// <returns>returns all stored data that is newer, or not part of, the gossipdata</returns>
        Task<MultiClusterData> Synchronize(MultiClusterData gossipdata);

    }
 

}
