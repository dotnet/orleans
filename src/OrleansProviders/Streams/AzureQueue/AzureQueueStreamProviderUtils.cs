/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Orleans.AzureUtils;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    /// <summary>
    /// Utility functions for azure queue Persistent stream provider.
    /// </summary>
    public class AzureQueueStreamProviderUtils
    {
        /// <summary>
        /// Helper method for unit testing.
        /// </summary>
        /// <param name="providerName"></param>
        /// <param name="deploymentId"></param>
        /// <param name="storageConnectionString"></param>
        /// <param name="logger"></param>
        /// <returns></returns>
        public static async Task DeleteAllUsedAzureQueues(string providerName, string deploymentId, string storageConnectionString, Logger logger)
        {
            if (deploymentId != null)
            {
                var queueMapper = new HashRingBasedStreamQueueMapper(AzureQueueAdapterFactory.NUM_QUEUES, providerName);
                List<QueueId> allQueues = queueMapper.GetAllQueues().ToList();

                if (logger != null) logger.Info("About to delete all {0} Stream Queues\n", allQueues.Count);
                foreach (var queueId in allQueues)
                {
                    var manager = new AzureQueueDataManager(queueId.ToString(), deploymentId, storageConnectionString);
                    await manager.DeleteQueue();
                }
            }
        }
    }
}
