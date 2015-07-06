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

using System;
using System.Threading.Tasks;
using Orleans.AzureUtils;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.PersistentStreams
{
    /// <summary>
    /// Delivery failure handler that writes failures to azure table storage.
    /// </summary>
    /// <typeparam name="TEntity"></typeparam>
    public class AzureTableStorageStreamFailureHandler<TEntity> : IStreamFailureHandler where TEntity : StreamDeliveryFailureEntity, new()
    {
        private static readonly Func<TEntity> DefaultCreateEntity = () => new TEntity();
        private readonly string deploymentId;
        private readonly AzureTableDataManager<TEntity> dataManager;
        private readonly Func<TEntity> createEntity;

        /// <summary>
        /// Delivery failure handler that writes failures to azure table storage.
        /// </summary>
        /// <param name="faultOnFailure"></param>
        /// <param name="deploymentId"></param>
        /// <param name="tableName"></param>
        /// <param name="storageConnectionString"></param>
        /// <param name="createEntity"></param>
        public AzureTableStorageStreamFailureHandler(bool faultOnFailure, string deploymentId, string tableName, string storageConnectionString, Func<TEntity> createEntity = null)
        {
            if (string.IsNullOrEmpty(deploymentId))
            {
                throw new ArgumentNullException("deploymentId");
            }
            if (string.IsNullOrEmpty(tableName))
            {
                throw new ArgumentNullException("tableName");
            }
            if (string.IsNullOrEmpty(storageConnectionString))
            {
                throw new ArgumentNullException("storageConnectionString");
            }
            this.deploymentId = deploymentId;
            ShouldFaultSubsriptionOnError = faultOnFailure;
            this.createEntity = createEntity ?? DefaultCreateEntity;
            dataManager = new AzureTableDataManager<TEntity>(tableName, storageConnectionString);
        }

        /// <summary>
        /// Indicates if the subscription should be put in a fauted state upon stream failures
        /// </summary>
        public bool ShouldFaultSubsriptionOnError { get; private set; }

        /// <summary>
        /// Initialization
        /// </summary>
        /// <returns></returns>
        public Task InitAsync()
        {
            return dataManager.InitTableAsync();
        }

        /// <summary>
        /// Should be called when an event could not be delivered to a consumer, after exhausting retry attempts.
        /// </summary>
        /// <param name="subscriptionId"></param>
        /// <param name="streamProviderName"></param>
        /// <param name="streamIdentity"></param>
        /// <param name="sequenceToken"></param>
        /// <returns></returns>
        public async Task OnDeliveryFailure(GuidId subscriptionId, string streamProviderName, IStreamIdentity streamIdentity,
            StreamSequenceToken sequenceToken)
        {
            if (subscriptionId == null)
            {
                throw new ArgumentNullException("subscriptionId");
            }
            if (string.IsNullOrWhiteSpace(streamProviderName))
            {
                throw new ArgumentNullException("streamProviderName");
            }
            if (streamIdentity == null)
            {
                throw new ArgumentNullException("streamIdentity");
            }

            var failureEntity = createEntity();
            failureEntity.SubscriptionId = subscriptionId.Guid;
            failureEntity.StreamProviderName = streamProviderName;
            failureEntity.StreamGuid = streamIdentity.Guid;
            failureEntity.StreamNamespace = streamIdentity.Namespace;
            failureEntity.SetSequenceToken(sequenceToken);
            failureEntity.SetPartitionKey(deploymentId);
            failureEntity.SetRowkey();
            await dataManager.CreateTableEntryAsync(failureEntity);
        }
    }
}
