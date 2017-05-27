using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Monitor track block pool related metrics
    /// </summary>
    public interface IObjectPoolMonitor
    {
        #region Event driven metrics
        /// <summary>
        /// Track every time cache allocate memory from block pool
        /// </summary>
        /// <param name="blockCount"></param>
        void TrackObjectAllocatedByCache(int blockCount);

        /// <summary>
        /// Track every time cache release memory back to block pool
        /// </summary>
        /// <param name="blockCount"></param>
        void TrackObjectReleasedFromCache(int blockCount);

        #endregion

        #region Periodically report block pool status 

        /// <summary>
        /// Periodically report block pool status
        /// </summary>
        /// <param name="totalBlocks">Total size of block pool</param>
        /// <param name="freeBlocks">Free memory in block pool which is available for cache to allocate</param>
        /// <param name="claimedBlocks">Memory claimed by cache in this block pool</param>
        void Report(long totalBlocks, long freeBlocks, long claimedBlocks);

        #endregion
    }
}
