using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlTypes;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.SqlUtils
{
    /// <summary>
    /// <remarks>This is public only to be usable to the statistics providers. Not intended for public use otherwise.</remarks>
    /// A class for all relational storages that support all systems stores : membership, reminders and statistics
    /// </summary>    
    public class RelationalOrleansQueries
    {
        /// <summary>
        /// the underlying storage
        /// </summary>
        private readonly IRelationalStorage storage;

        /// <summary>
        /// When inserting statistics and generating a batch insert clause, these are the columns in the statistics
        /// table that will be updated with multiple values. The other ones are updated with one value only.
        /// </summary>
        private readonly static string[] InsertStatisticsMultiupdateColumns = new[] { "@isDelta", "@statValue", "@statistic" };
        
        /// <summary>
        /// the orleans functional queries
        /// </summary>
        private readonly OrleansQueries orleansQueries;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="storage">the underlying relational storage</param>
        /// <param name="orleansQueries">Orleans functional queries</param>
        private RelationalOrleansQueries(IRelationalStorage storage, OrleansQueries orleansQueries)
        {
            this.storage = storage;
            this.orleansQueries = orleansQueries;
        }

        /// <summary>
        /// Creates an instance of a database of type <see cref="RelationalOrleansQueries"/> and Initializes Orleans queries from the database. 
        /// Orleans uses only these queries and the variables therein, nothing more.
        /// </summary>
        /// <param name="invariantName">The invariant name of the connector for this database.</param>
        /// <param name="connectionString">The connection string this database should use for database operations.</param>
        public static Task<RelationalOrleansQueries> CreateInstance(string invariantName, string connectionString)
        {
            var storage = RelationalStorage.CreateInstance(invariantName, connectionString);

            return CreateInstance(storage);
        }

        /// <summary>
        /// Creates an instance of a database of type <see cref="RelationalOrleansQueries"/> and Initializes Orleans queries from the database. 
        /// Orleans uses only these queries and the variables therein, nothing more.
        /// </summary>
        /// <param name="storage">the underlying storage instance</param>
        public static async Task<RelationalOrleansQueries> CreateInstance(IRelationalStorage storage)
        {
            //this will probably be the same for all relational dbs
            const string orleansQueriesSelectTemplate = "SELECT QueryKey, QueryText FROM OrleansQuery;";
            var orleansQueries = new OrleansQueries();
            var propertiesDictionary = typeof(OrleansQueries).GetFields().ToDictionary(p => p.Name, p => p);
            var populatedKeys = await storage.ReadAsync(orleansQueriesSelectTemplate, _ => { }, (selector, _) =>
            {
                var queryKey = selector.GetValue<string>("QueryKey");
                var queryText = selector.GetValue<string>("QueryText");
                propertiesDictionary[queryKey].SetValue(orleansQueries, queryText);
                return queryKey;
            }).ConfigureAwait(continueOnCapturedContext: false);

            //Check that all the required keys are loaded and throw an exception giving the keys expected but not loaded.
            var missingQueryKeys = populatedKeys.Except(propertiesDictionary.Keys);
            if (missingQueryKeys.Any())
            {
                throw new ArgumentException(string.Format("Not all required queries found when loading from the database. Missing are: {0}", string.Join(",", missingQueryKeys)));
            }

            return new RelationalOrleansQueries(storage, orleansQueries);
        }


        /// <summary>
        /// Either inserts or updates a silo metrics row.
        /// </summary>
        /// <param name="deploymentId">The deployment ID.</param>
        /// <param name="siloId">The silo ID.</param>
        /// <param name="gateway">The gateway information.</param>
        /// <param name="siloAddress">The silo address information.</param>
        /// <param name="hostName">The hostname.</param>
        /// <param name="siloMetrics">The silo metrics to be either updated or inserted.</param>
        /// <returns></returns>
        public async Task UpsertSiloMetricsAsync(string deploymentId, string siloId, IPEndPoint gateway, SiloAddress siloAddress, string hostName, ISiloPerformanceMetrics siloMetrics)
        {
            await storage.ExecuteAsync(orleansQueries.UpsertSiloMetricsKey, command =>
            {
                var deploymentIdParameter = CreateDeploymentIdParameter(command, deploymentId);
                command.Parameters.Add(deploymentIdParameter);

                var clientIdParameter = CreateSiloIdParameter(command, siloId);
                command.Parameters.Add(clientIdParameter);

                var addressParameter = CreateAddressParameter(command, siloAddress.Endpoint.Address);
                command.Parameters.Add(addressParameter);

                var portParameter = CreatePortParameter(command, siloAddress.Endpoint.Port);
                command.Parameters.Add(portParameter);

                var generationParameter = CreateGenerationParameter(command, siloAddress.Generation);
                command.Parameters.Add(generationParameter);

                var hostNameParameter = CreateHostNameParameter(command, hostName);
                command.Parameters.Add(hostNameParameter);

                var gatewayAddressParameter = CreateGatewayAddressParameter(command, gateway != null ? gateway.Address : null);
                command.Parameters.Add(gatewayAddressParameter);

                var gatewayPortParameter = CreateGatewayPortParameter(command, gateway != null ? gateway.Port : 0);
                command.Parameters.Add(gatewayPortParameter);

                var cpuUsageParameter = CreateCpuUsageParameter(command, siloMetrics.CpuUsage);
                command.Parameters.Add(cpuUsageParameter);

                var memoryUsageParameter = CreateMemoryUsageParameter(command, siloMetrics.MemoryUsage);
                command.Parameters.Add(memoryUsageParameter);

                var activationsCountParameter = CreateActivationsCountParameter(command, siloMetrics.ActivationCount);
                command.Parameters.Add(activationsCountParameter);

                var recentlyUsedActivationsCountParameter = CreateRecentlyUsedActivationsCountParameter(command, siloMetrics.RecentlyUsedActivationCount);
                command.Parameters.Add(recentlyUsedActivationsCountParameter);

                var sendQueueLengthParameter = CreateSendQueueUsageParameter(command, siloMetrics.SendQueueLength);
                command.Parameters.Add(sendQueueLengthParameter);

                var receiveQueueParameter = CreateReceiveQueueLengthParameter(command, siloMetrics.ReceiveQueueLength);
                command.Parameters.Add(receiveQueueParameter);

                var sentMessagesCountParameter = CreateSentMessagesCountParameter(command, siloMetrics.SentMessages);
                command.Parameters.Add(sentMessagesCountParameter);

                var receivedMessagesCountParameter = CreateReceivedMessagesCountParameter(command, siloMetrics.ReceivedMessages);
                command.Parameters.Add(receivedMessagesCountParameter);

                var requestQueueLengthParameter = CreateRequestQueueLengthParameter(command, siloMetrics.RequestQueueLength);
                command.Parameters.Add(requestQueueLengthParameter);

                var isOverloadedParameter = CreateIsOverloadedParameter(command, siloMetrics.IsOverloaded);
                command.Parameters.Add(isOverloadedParameter);

                var clientCountParameter = CreateClientCountParameter(command, siloMetrics.ClientCount);
                command.Parameters.Add(clientCountParameter);
            }).ConfigureAwait(continueOnCapturedContext: false);
        }


        /// <summary>
        /// Either inserts or updates a silo metrics row. 
        /// </summary>
        /// <param name="deploymentId">The deployment ID.</param>
        /// <param name="clientId">The client ID.</param>
        /// <param name="clientAddress">The client address information.</param>
        /// <param name="hostName">The hostname.</param>
        /// <param name="clientMetrics">The client metrics to be either updated or inserted.</param>
        /// <returns></returns>
        public async Task UpsertReportClientMetricsAsync(string deploymentId, string clientId, IPAddress clientAddress, string hostName, IClientPerformanceMetrics clientMetrics)
        {
            await storage.ExecuteAsync(orleansQueries.UpsertReportClientMetricsKey, command =>
            {
                var deploymentIdParameter = CreateDeploymentIdParameter(command, deploymentId);
                command.Parameters.Add(deploymentIdParameter);

                var clientIdParameter = CreateClientIdParameter(command, clientId);
                command.Parameters.Add(clientIdParameter);

                var addressParameter = CreateAddressParameter(command, clientAddress);
                command.Parameters.Add(addressParameter);

                var hostNameParameter = CreateHostNameParameter(command, hostName);
                command.Parameters.Add(hostNameParameter);

                var cpuUsageParameter = CreateCpuUsageParameter(command, clientMetrics.CpuUsage);
                command.Parameters.Add(cpuUsageParameter);

                var memoryUsageParameter = CreateMemoryUsageParameter(command, clientMetrics.MemoryUsage);
                command.Parameters.Add(memoryUsageParameter);

                var sendQueueLengthParameter = CreateSendQueueUsageParameter(command, clientMetrics.SendQueueLength);
                command.Parameters.Add(sendQueueLengthParameter);

                var receiveQueueParameter = CreateReceiveQueueLengthParameter(command, clientMetrics.ReceiveQueueLength);
                command.Parameters.Add(receiveQueueParameter);

                var sentMessagesCountParameter = CreateSentMessagesCountParameter(command, clientMetrics.SentMessages);
                command.Parameters.Add(sentMessagesCountParameter);

                var receivedMessagesCountParameter = CreateReceivedMessagesCountParameter(command, clientMetrics.ReceivedMessages);
                command.Parameters.Add(receivedMessagesCountParameter);

                var connectionGatewayCountParameter = CreateConnectionGatewayCountParameter(command, clientMetrics.ConnectedGatewayCount);
                command.Parameters.Add(connectionGatewayCountParameter);
            }).ConfigureAwait(continueOnCapturedContext: false);
        }


        /// <summary>
        /// Inserts the given statistics counters to the Orleans database.
        /// </summary>
        /// <param name="deploymentId">The deployment ID.</param>
        /// <param name="hostName">The hostname.</param>
        /// <param name="siloOrClientName">The silo or client name.</param>
        /// <param name="id">The silo address or client ID.</param>
        /// <param name="counters">The counters to be inserted.</param>        
        public async Task InsertStatisticsCountersAsync(string deploymentId, string hostName, string siloOrClientName, string id, IEnumerable<ICounter> counters)
        {
            var queryTemplate = orleansQueries.InsertOrleansStatisticsKey;
            //Zero statistic values mean either that the system is not running or no updates. Such values are not inserted and pruned
            //here so that no insert query or parameters are generated.
            var countersList = counters.Where(i => !"0".Equals(i.IsValueDelta ? i.GetDeltaString() : i.GetValueString())).ToList();
            if(countersList.Count == 0) { return; }

            //Note that the following is almost the same as RelationalStorageExtensions.ExecuteMultipleInsertIntoAsync
            //the only difference being that some columns are skipped. Likely it would be beneficial to introduce
            //a "skip list" to RelationalStorageExtensions.ExecuteMultipleInsertIntoAsync.

            //The template contains an insert for online. The part after SELECT is copied
            //out so that certain parameters can be multiplied by their count. Effectively
            //this turns a query of type (transaction details vary by vendor)
            //BEGIN TRANSACTION; INSERT INTO [OrleansStatisticsTable] <columns> SELECT <variables>; COMMIT TRANSACTION;
            //to BEGIN TRANSACTION; INSERT INTO [OrleansStatisticsTable] <columns> SELECT <variables>; UNION ALL <variables> COMMIT TRANSACTION;
            //where the UNION ALL is multiplied as many times as there are counters to insert.
            int startFrom = queryTemplate.IndexOf("SELECT", StringComparison.Ordinal) + "SELECT".Length + 1; //This +1 is to have a space between SELECT and the first parameter name to not to have a SQL syntax error.
            int lastSemicolon = queryTemplate.LastIndexOf(";");
            int endTo = lastSemicolon > 0 ? queryTemplate.LastIndexOf(";", lastSemicolon - 1, StringComparison.Ordinal) : -1;
            var template = queryTemplate.Substring(startFrom, endTo - startFrom);
            var parameterNames = template.Split(new[] { "," }, StringSplitOptions.RemoveEmptyEntries).Select(i => i.Trim()).ToArray();
            var collectionOfParametersToBeUnionized = new List<string>();
            var parametersToBeUnioned = new string[parameterNames.Length];
            for(int counterIndex = 0; counterIndex < countersList.Count; ++counterIndex)
            {
                for(int parameterNameIndex = 0; parameterNameIndex < parameterNames.Length; ++parameterNameIndex)
                {
                    if(InsertStatisticsMultiupdateColumns.Contains(parameterNames[parameterNameIndex]))
                    {
                        //These parameters change for each row. The format is
                        //@statValue0, @statValue1, @statValue2, ... @statValue{countersList.Count}.
                        parametersToBeUnioned[parameterNameIndex] = string.Format("{0}{1}", parameterNames[parameterNameIndex], counterIndex);
                    }
                    else
                    {
                        //These parameters remain constant for every and each row.
                        parametersToBeUnioned[parameterNameIndex] = parameterNames[parameterNameIndex];
                    }
                }
                collectionOfParametersToBeUnionized.Add(string.Format("{0}", string.Join(",", parametersToBeUnioned)));
            }

            var storageConsts = DbConstantsStore.GetDbConstants(storage.InvariantName);
            var query = queryTemplate.Replace(template, string.Join(storageConsts.UnionAllSelectTemplate, collectionOfParametersToBeUnionized));
            await storage.ExecuteAsync(query, command =>
            {                
                var deploymentIdParameter = CreateDeploymentIdParameter(command, deploymentId);
                command.Parameters.Add(deploymentIdParameter);

                var idParameter = CreateIdParameter(command, id);
                command.Parameters.Add(idParameter);

                var hostNameParameter = CreateHostNameParameter(command, hostName);
                command.Parameters.Add(hostNameParameter);

                var nameParameter = CreateNameParameter(command, siloOrClientName);
                command.Parameters.Add(nameParameter);

                for(int i = 0; i < countersList.Count; ++i)
                {
                    var isDeltaParameter = CreateIsDeltaParameter(command, countersList[i].IsValueDelta, i);
                    command.Parameters.Add(isDeltaParameter);

                    var statParameter = CreateStatParameter(command, countersList[i].IsValueDelta ? countersList[i].GetDeltaString() : countersList[i].GetValueString(), i);
                    command.Parameters.Add(statParameter);

                    var statisticNameParameter = CreateStatisticNameParameter(command, countersList[i].Name, i);
                    command.Parameters.Add(statisticNameParameter);
                }
            }).ConfigureAwait(continueOnCapturedContext: false);
        }


        /// <summary>
        /// Reads Orleans reminder data from the tables.
        /// </summary>
        /// <param name="serviceId">The service ID.</param>
        /// <param name="grainRef">The grain reference (ID).</param>
        /// <returns>Reminder table data.</returns>
        internal async Task<ReminderTableData> ReadReminderRowsAsync(string serviceId, GrainReference grainRef)
        {
            var ret = await storage.ReadAsync(orleansQueries.ReadReminderRowsKey, command =>
            {
                var serviceIdParameter = CreateServiceIdParameter(command, serviceId);
                command.Parameters.Add(serviceIdParameter);

                var grainIdParameter = CreateGrainIdParameter(command, grainRef.ToKeyString());
                command.Parameters.Add(grainIdParameter);
            }, (selector, _) =>
            {
                return CreateReminderEntry(selector);
            }).ConfigureAwait(continueOnCapturedContext: false);

            return new ReminderTableData(ret.Where(i => i != null).ToList());
        }


        /// <summary>
        /// Reads Orleans reminder data from the tables.
        /// </summary>
        /// <param name="serviceId">The service ID.</param>
        /// <param name="beginHash">The begin hash.</param>
        /// <param name="endHash">The end hash.</param>
        /// <returns>Reminder table data.</returns>
        internal async Task<ReminderTableData> ReadReminderRowsAsync(string serviceId, uint beginHash, uint endHash)
        {
            var query = beginHash < endHash ? orleansQueries.ReadRangeRows1Key : orleansQueries.ReadRangeRows2Key;
            var ret = await storage.ReadAsync(query, command =>
            {                
                var serviceIdParameter = CreateServiceIdParameter(command, serviceId);
                command.Parameters.Add(serviceIdParameter);

                var beginHashParameter = CreateBeginHashParameter(command, beginHash);
                command.Parameters.Add(beginHashParameter);

                var endHashParameter = CreateEndHashParameter(command, endHash);
                command.Parameters.Add(endHashParameter);
            }, (selector, _) =>
            {
                return CreateReminderEntry(selector);
            }).ConfigureAwait(continueOnCapturedContext: false);

            return new ReminderTableData(ret.Where(i => i != null).ToList());
        }


        /// <summary>
        /// Reads one row of reminder data.
        /// </summary>
        /// <param name="serviceId">Service ID.</param>
        /// <param name="grainRef">The grain reference (ID).</param>
        /// <param name="reminderName">The reminder name to retrieve.</param>
        /// <returns>A remainder entry.</returns>
        internal async Task<ReminderEntry> ReadReminderRowAsync( string serviceId, GrainReference grainRef, string reminderName)
        {
            var ret = await storage.ReadAsync(orleansQueries.ReadReminderRowKey, command =>
            {
                var serviceIdParameter = CreateServiceIdParameter(command, serviceId);
                command.Parameters.Add(serviceIdParameter);

                var grainIdParameter = CreateGrainIdParameter(command, grainRef.ToKeyString());
                command.Parameters.Add(grainIdParameter);

                var reminderNameParameter = CreateReminderName(command, reminderName);
                command.Parameters.Add(reminderNameParameter);
            }, (selector, _) =>
            {
                return CreateReminderEntry(selector);
            }).ConfigureAwait(continueOnCapturedContext: false);

            return ret != null ? ret.FirstOrDefault() : null;
        }


        /// <summary>
        /// Either inserts or updates a reminder row.
        /// </summary>
        /// <param name="serviceId">The service ID.</param>
        /// <param name="grainRef">The grain reference (ID).</param>
        /// <param name="reminderName">The reminder name to retrieve.</param>
        /// <param name="startTime">Start time of the reminder.</param>
        /// <param name="period">Period of the reminder.</param>
        /// <returns>The new etag of the either or updated or inserted reminder row.</returns>
        internal async Task<string> UpsertReminderRowAsync(string serviceId, GrainReference grainRef, string reminderName, DateTime startTime, TimeSpan period)
        {
            var ret = await storage.ReadAsync(orleansQueries.UpsertReminderRowKey, command =>
            {
                var serviceIdParameter = CreateServiceIdParameter(command, serviceId);
                command.Parameters.Add(serviceIdParameter);

                var grainIdParameter = CreateGrainIdParameter(command, grainRef.ToKeyString());
                command.Parameters.Add(grainIdParameter);

                var reminderNameParameter = CreateReminderName(command, reminderName);
                command.Parameters.Add(reminderNameParameter);

                var startTimeParameter = CreateStartTimeParameter(command, startTime);
                command.Parameters.Add(startTimeParameter);

                var periodParameter = CreatePeriodParameter(command, period);
                command.Parameters.Add(periodParameter);

                var grainIdConsistentHashParameter = CreateGrainIdConsistentHashParameter(command, grainRef.GetUniformHashCode());
                command.Parameters.Add(grainIdConsistentHashParameter);
            }, (selector, _) =>
            {
                return Convert.ToBase64String(selector.GetValue<byte[]>("ETag"));
            }).ConfigureAwait(continueOnCapturedContext: false);

            return ret != null ? ret.FirstOrDefault() : null;
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="serviceId">Service ID.</param>
        /// <param name="grainRef"></param>
        /// <param name="reminderName"></param>
        /// <param name="etag"></param>
        /// <returns></returns>
        internal async Task<bool> DeleteReminderRowAsync(string serviceId, GrainReference grainRef, string reminderName, string etag)
        {
            var ret = await storage.ReadAsync(orleansQueries.DeleteReminderRowKey, command =>
            {
                var serviceIdParameter = CreateServiceIdParameter(command, serviceId);
                command.Parameters.Add(serviceIdParameter);

                var grainIdParameter = CreateGrainIdParameter(command, grainRef.ToKeyString());
                command.Parameters.Add(grainIdParameter);

                var reminderNameParameter = CreateReminderName(command, reminderName);
                command.Parameters.Add(reminderNameParameter);

                var etagParameter = CreateEtagParameter(command, etag);
                command.Parameters.Add(etagParameter);
            }, (selector, _) =>
            {
                return selector.GetBoolean(0);
            }).ConfigureAwait(continueOnCapturedContext: false);

            return ret.First();
        }


        /// <summary>
        /// deletes all reminders rows of a service id
        /// </summary>
        /// <param name="serviceId"></param>
        /// <returns></returns>
        internal async Task DeleteReminderRowsAsync(string serviceId)
        {
            await storage.ExecuteAsync(orleansQueries.DeleteReminderRowsKey, command =>
            {
                var serviceIdParameter = CreateServiceIdParameter(command, serviceId);
                command.Parameters.Add(serviceIdParameter);
            }).ConfigureAwait(continueOnCapturedContext: false);
        }



        /// <summary>
        /// Lists active gateways. Used mainly by Orleans clients.
        /// </summary>
        /// <param name="deploymentId">The deployment for which to query the gateways.</param>
        /// <returns>The gateways for the silo.</returns>
        internal async Task<IList<Uri>> ActiveGatewaysAsync(string deploymentId)
        {
            var ret = await storage.ReadAsync(orleansQueries.ActiveGatewaysQueryKey, command =>
            {
                var siloIdParameter = CreateDeploymentIdParameter(command, deploymentId);
                command.Parameters.Add(siloIdParameter);

                var statusParameter = CreateStatusParameter(command, SiloStatus.Active);
                command.Parameters.Add(statusParameter);
            }, (selector, _) =>
            {
                var ip = selector.GetValue<string>("Address");
                var port = selector.GetValue<int>("ProxyPort");
                var gen = selector.GetValue<int>("Generation");

                return SiloAddress.New(new IPEndPoint(IPAddress.Parse(ip), port), gen).ToGatewayUri();

            }).ConfigureAwait(continueOnCapturedContext: false);

            return ret.ToList();
        }


        /// <summary>
        /// Queries Orleans membership data.
        /// </summary>
        /// <param name="deploymentId">The deployment for which to query data.</param>
        /// <param name="key">Silo data used as parameters in the query.</param>
        /// <returns>Membership table data.</returns>
        internal async Task<MembershipTableData> MembershipDataAsync(string deploymentId, SiloAddress key)
        {
            var ret = await storage.ReadAsync(orleansQueries.MembershipReadRowKey, command =>
            {
                var siloIdParameter = CreateDeploymentIdParameter(command, deploymentId);
                command.Parameters.Add(siloIdParameter);

                var addressParameter = CreateAddressParameter(command, key.Endpoint.Address);
                command.Parameters.Add(addressParameter);

                var portParameter = CreatePortParameter(command, key.Endpoint.Port);
                command.Parameters.Add(portParameter);

                var generationParameter = CreateGenerationParameter(command, key.Generation);
                command.Parameters.Add(generationParameter);
            }, (selector, _) =>
            {
                return CreateMembershipEntry(selector);
            }).ConfigureAwait(continueOnCapturedContext: false);

            //All the rows have the same Version (.Item3) and VersionETag (.Item4) information.
            //If .Item1 of the first element has no data, the MembershipEntry collection is empty.
            var membershipVersionData = ret.First();
            var membershipData = membershipVersionData.Item1 == null ? Enumerable.Empty<Tuple<MembershipEntry, string>>() : ret.Select(i => Tuple.Create(i.Item1, i.Item2));
            return new MembershipTableData(membershipData.ToList(), new TableVersion(membershipVersionData.Item3, membershipVersionData.Item4));
        }


        /// <summary>
        /// returns all membership data for a deployment id
        /// </summary>
        /// <param name="deploymentId"></param>
        /// <returns></returns>
        internal async Task<MembershipTableData> AllMembershipDataAsync(string deploymentId)
        {
            var ret = await storage.ReadAsync(orleansQueries.MembershipReadAllKey, command =>
            {
                var deploymentIdParameter = CreateDeploymentIdParameter(command, deploymentId);
                command.Parameters.Add(deploymentIdParameter);
            }, (selector, _) =>
            {
                return CreateMembershipEntry(selector);
            }).ConfigureAwait(continueOnCapturedContext: false);

            //All the rows have the same Version (.Item3) and VersionETag (.Item4) information.
            //If .Item1 of the first element has no data, the MembershipEntry collection is empty.
            var membershipVersionData = ret.First();
            var membershipData = membershipVersionData.Item1 == null ? Enumerable.Empty<Tuple<MembershipEntry, string>>() : ret.Select(i => Tuple.Create(i.Item1, i.Item2));
            return new MembershipTableData(membershipData.ToList(), new TableVersion(membershipVersionData.Item3, membershipVersionData.Item4));
        }


        /// <summary>
        /// deletes all membership entries for a deployment id
        /// </summary>
        /// <param name="deploymentId"></param>
        /// <returns></returns>
        internal async Task DeleteMembershipTableEntriesAsync(string deploymentId)
        {
            await storage.ExecuteAsync(orleansQueries.DeleteMembershipTableEntriesKey, command =>
            {
                var siloIdParameter = CreateDeploymentIdParameter(command, deploymentId);
                command.Parameters.Add(siloIdParameter);
            }).ConfigureAwait(continueOnCapturedContext: false);
        }


        /// <summary>
        /// Updates IAmAlive for a silo
        /// </summary>
        /// <param name="deploymentId"></param>
        /// <param name="membershipEntry"></param>
        /// <returns></returns>
        internal async Task UpdateIAmAliveTimeAsync(string deploymentId, MembershipEntry membershipEntry)
        {
            await storage.ExecuteAsync(orleansQueries.UpdateIAmAlivetimeKey, command =>
            {
                var siloIdParameter = CreateDeploymentIdParameter(command, deploymentId);
                command.Parameters.Add(siloIdParameter);

                var iAmAliveTimeParameter = CreateIAmAliveTimeParameter(command, membershipEntry.IAmAliveTime);
                command.Parameters.Add(iAmAliveTimeParameter);

                var addressParameter = CreateAddressParameter(command, membershipEntry.SiloAddress.Endpoint.Address);
                command.Parameters.Add(addressParameter);

                var portParameter = CreatePortParameter(command, membershipEntry.SiloAddress.Endpoint.Port);
                command.Parameters.Add(portParameter);

                var generationParameter = CreateGenerationParameter(command, membershipEntry.SiloAddress.Generation);
                command.Parameters.Add(generationParameter);
            }).ConfigureAwait(continueOnCapturedContext: false);
        }


        /// <summary>
        /// Inserts a version row if one does not already exist.
        /// </summary>
        /// <param name="deploymentId">The deployment for which to query data.</param>
        /// <param name="version">The version information to insert.</param>
        /// <returns><em>TRUE</em> if a row was inserted. <em>FALSE</em> otherwise.</returns>
        internal async Task<bool> InsertMembershipVersionRowAsync(string deploymentId, int version)
        {
            var ret = await storage.ReadAsync(orleansQueries.InsertMembershipVersionKey, command =>
            {
                var siloIdParameter = CreateDeploymentIdParameter(command, deploymentId);
                command.Parameters.Add(siloIdParameter);

                var versionParameter = CreateVersionParameter(command, version);
                command.Parameters.Add(versionParameter);
            }, (selector, _) => { return selector.GetBoolean(0); }).ConfigureAwait(continueOnCapturedContext: false);

            return ret.First();
        }


        /// <summary>
        /// Inserts a membership row if one does not already exist.
        /// </summary>
        /// <param name="deploymentId">The deployment with which to insert row.</param>
        /// <param name="membershipEntry">The membership entry data to insert.</param>
        /// <param name="version">The version data to insert.</param>
        /// <returns><em>TRUE</em> if insert succeeds. <em>FALSE</em> otherwise.</returns>
        internal async Task<bool> InsertMembershipRowAsync(string deploymentId, MembershipEntry membershipEntry, TableVersion version)
        {
            var ret = await storage.ReadAsync(orleansQueries.InsertMembershipKey, command =>
            {
                var siloIdParameter = CreateDeploymentIdParameter(command, deploymentId);
                command.Parameters.Add(siloIdParameter);

                var versionParameter = CreateVersionParameter(command, version.Version);
                command.Parameters.Add(versionParameter);

                var versionEtagParameter = CreateVersionEtagParameter(command, version.VersionEtag);
                command.Parameters.Add(versionEtagParameter);

                //The insert membership row part.
                var addressParameter = CreateAddressParameter(command, membershipEntry.SiloAddress.Endpoint.Address);
                command.Parameters.Add(addressParameter);

                var portParameter = CreatePortParameter(command, membershipEntry.SiloAddress.Endpoint.Port);
                command.Parameters.Add(portParameter);

                var generationParameter = CreateGenerationParameter(command, membershipEntry.SiloAddress.Generation);
                command.Parameters.Add(generationParameter);

                var hostNameParameter = CreateHostNameParameter(command, membershipEntry.HostName);
                command.Parameters.Add(hostNameParameter);

                var statusParameter = CreateStatusParameter(command, membershipEntry.Status);
                command.Parameters.Add(statusParameter);

                var proxyPortParameter = CreateProxyPortParameter(command, membershipEntry.ProxyPort);
                command.Parameters.Add(proxyPortParameter);

                var roleNameParameter = CreateRoleNameParameter(command, membershipEntry.RoleName);
                command.Parameters.Add(roleNameParameter);

                var instanceNameParameter = CreateInstanceNameParameter(command, membershipEntry.InstanceName);
                command.Parameters.Add(instanceNameParameter);

                var updateZoneParameter = CreateUpdateZoneParameter(command, membershipEntry.UpdateZone);
                command.Parameters.Add(updateZoneParameter);

                var faultZoneParameter = CreateFaultZoneParameter(command, membershipEntry.FaultZone);
                command.Parameters.Add(faultZoneParameter);

                var startTimeParameter = CreateStartTimeParameter(command, membershipEntry.StartTime);
                command.Parameters.Add(startTimeParameter);

                var iAmAliveTimeParameter = CreateIAmAliveTimeParameter(command, membershipEntry.IAmAliveTime);
                command.Parameters.Add(iAmAliveTimeParameter);
                
                var suspectingSilosParameter = CreateSuspectingSilosParameter(command, membershipEntry);
                command.Parameters.Add(suspectingSilosParameter);

                var suspectingTimesParameter = CreateSuspectingTimesParameter(command, membershipEntry);
                command.Parameters.Add(suspectingTimesParameter);

            }, (selector, _) => { return selector.GetBoolean(0); }).ConfigureAwait(continueOnCapturedContext: false);

            return ret.First();
        }


        /// <summary>
        /// Updates membership row data.
        /// </summary>
        /// <param name="deploymentId">The deployment with which to insert row.</param>
        /// <param name="etag">The etag of which to use to check if the membership data being updated is not stale.</param>
        /// <param name="membershipEntry">The membership data to used to update database.</param>
        /// <param name="version">The membership version used to update database.</param>
        /// <returns><em>TRUE</em> if update SUCCEEDS. <em>FALSE</em> ot</returns>
        internal async Task<bool> UpdateMembershipRowAsync(string deploymentId, string etag, MembershipEntry membershipEntry, TableVersion version)
        {
            var ret = await storage.ReadAsync(orleansQueries.UpdateMembershipKey, command =>
            {
                var siloIdParameter = CreateDeploymentIdParameter(command, deploymentId);
                command.Parameters.Add(siloIdParameter);

                var versionParameter = CreateVersionParameter(command, version.Version);
                command.Parameters.Add(versionParameter);

                var versionEtagParameter = CreateVersionEtagParameter(command, version.VersionEtag);
                command.Parameters.Add(versionEtagParameter);

                //The insert membership row part.
                var etagParameter = CreateEtagParameter(command, etag);
                command.Parameters.Add(etagParameter);

                var addressParameter = CreateAddressParameter(command, membershipEntry.SiloAddress.Endpoint.Address);
                command.Parameters.Add(addressParameter);

                var portParameter = CreatePortParameter(command, membershipEntry.SiloAddress.Endpoint.Port);
                command.Parameters.Add(portParameter);

                var generationParameter = CreateGenerationParameter(command, membershipEntry.SiloAddress.Generation);
                command.Parameters.Add(generationParameter);

                var hostNameParameter = CreateHostNameParameter(command, membershipEntry.HostName);
                command.Parameters.Add(hostNameParameter);

                var statusParameter = CreateStatusParameter(command, membershipEntry.Status);
                command.Parameters.Add(statusParameter);

                var proxyPortParameter = CreateProxyPortParameter(command, membershipEntry.ProxyPort);
                command.Parameters.Add(proxyPortParameter);

                var roleNameParameter = CreateRoleNameParameter(command, membershipEntry.RoleName);
                command.Parameters.Add(roleNameParameter);

                var instanceNameParameter = CreateInstanceNameParameter(command, membershipEntry.InstanceName);
                command.Parameters.Add(instanceNameParameter);

                var updateZoneParameter = CreateUpdateZoneParameter(command, membershipEntry.UpdateZone);
                command.Parameters.Add(updateZoneParameter);

                var faultZoneParameter = CreateFaultZoneParameter(command, membershipEntry.FaultZone);
                command.Parameters.Add(faultZoneParameter);

                var startTimeParameter = CreateStartTimeParameter(command, membershipEntry.StartTime);
                command.Parameters.Add(startTimeParameter);

                var iAmAliveTimeParameter = CreateIAmAliveTimeParameter(command, membershipEntry.IAmAliveTime);
                command.Parameters.Add(iAmAliveTimeParameter);
                
                var suspectingSilosParameter = CreateSuspectingSilosParameter(command, membershipEntry);
                command.Parameters.Add(suspectingSilosParameter);

                var suspectingTimesParameter = CreateSuspectingTimesParameter(command, membershipEntry);
                command.Parameters.Add(suspectingTimesParameter);

            }, (selector, _) => { return selector.GetBoolean(0); }).ConfigureAwait(continueOnCapturedContext: false);

            return ret.First();
        }


        private static ReminderEntry CreateReminderEntry(IDataRecord record)
        {
            //Having non-null field, GrainId, means with the query filter options, an entry was found.
            if(record.GetValueOrDefault<string>("GrainId") != null)
            {
                return new ReminderEntry
                {
                    GrainRef = GrainReference.FromKeyString(record.GetValue<string>("GrainId")),
                    ReminderName = record.GetValue<string>("ReminderName"),
                    StartAt = record.GetValue<DateTime>("StartTime"),
                    Period = TimeSpan.FromMilliseconds(record.GetValue<int>("Period")),
                    ETag = Convert.ToBase64String(record.GetValue<byte[]>("ETag"))
                };
            }

            return null;
        }


        private Tuple<MembershipEntry, string, int, string> CreateMembershipEntry(IDataRecord record)
        {
            //TODO: This is a bit of hack way to check in the current version if there's membership data or not, but if there's a start time, there's member.            
            DateTime? startTime = record.GetValueOrDefault<DateTime?>("StartTime");
            MembershipEntry entry = null;
            string etag = null;
            if(startTime.HasValue)
            {
                etag = Convert.ToBase64String(record.GetValue<byte[]>("ETag"));
                int port = record.GetValue<int>("Port");
                int generation = record.GetValue<int>("Generation");
                string address = record.GetValue<string>("Address");
                entry = new MembershipEntry
                {
                    SiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Parse(address), port), generation),
                    HostName = record.GetValueOrDefault<string>("HostName"),
                    Status = record.GetValue<SiloStatus>("Status"),
                    ProxyPort = record.GetValueOrDefault<int>("ProxyPort"),
                    RoleName = record.GetValue<string>("RoleName"),
                    InstanceName = record.GetValue<string>("InstanceName"),
                    UpdateZone = record.GetValue<int>("UpdateZone"),
                    StartTime = startTime.GetValueOrDefault(),
                    FaultZone = record.GetValueOrDefault<int>("FaultZone"),
                    IAmAliveTime = record.GetValueOrDefault<DateTime>("IAmAliveTime")
                };

                //TODO: Refactor the database with regard to these.                
                string suspectingSilo = record.GetValueOrDefault<string>("SuspectingSilos");
                string suspectingTime = record.GetValueOrDefault<string>("SuspectingTimes");
                List<SiloAddress> suspectingSilos = new List<SiloAddress>();
                List<DateTime> suspectingTimes = new List<DateTime>();
                if(!string.IsNullOrWhiteSpace(suspectingSilo))
                {
                    string[] silos = suspectingSilo.Split('|');
                    foreach(string silo in silos)
                    {
                        suspectingSilos.Add(SiloAddress.FromParsableString(silo));
                    }
                }

                if(!string.IsNullOrWhiteSpace(suspectingTime))
                {
                    string[] times = suspectingTime.Split('|');
                    foreach(string time in times)
                    {
                        suspectingTimes.Add(TraceLogger.ParseDate(time));
                    }
                }

                if(suspectingSilos.Count != suspectingTimes.Count)
                {
                    throw new OrleansException(string.Format("SuspectingSilos.Length of {0} as read from SQL table is not equal to SuspectingTimes.Length of {1}", suspectingSilos.Count, suspectingTimes.Count));
                }

                for(int i = 0; i < suspectingSilos.Count; ++i)
                {
                    entry.AddSuspector(suspectingSilos[i], suspectingTimes[i]);
                }
            }

            int tableVersion = (int)record.GetValueOrDefault<long>("Version");
            string versionETag = Convert.ToBase64String(record.GetValue<byte[]>("VersionETag"));

            return Tuple.Create(entry, etag, tableVersion, versionETag);
        }


        private static IDbDataParameter CreateBeginHashParameter(IDbCommand command, uint beginHash)
        {            
            return command.CreateParameter(ParameterDirection.Input, "beginHash", (int)beginHash);
        }


        private static IDbDataParameter CreateEndHashParameter(IDbCommand command, uint endHash)
        {            
            return command.CreateParameter(ParameterDirection.Input, "endHash", (int)endHash);
        }


        private static IDbDataParameter CreateServiceIdParameter(IDbCommand command, string serviceId)
        {            
            return command.CreateParameter(ParameterDirection.Input, "serviceId", serviceId);
        }


        private static IDbDataParameter CreateGrainIdParameter(IDbCommand command, string grainId)
        {            
            return command.CreateParameter(ParameterDirection.Input, "grainId", grainId);
        }


        private static IDbDataParameter CreateDeploymentIdParameter(IDbCommand command, string deploymentId)
        {            
            return command.CreateParameter(ParameterDirection.Input, "deploymentId", deploymentId);
        }


        private static IDbDataParameter CreateSiloIdParameter(IDbCommand command, string siloId)
        {
            return command.CreateParameter(ParameterDirection.Input, "siloId", siloId);
        }


        private static IDbDataParameter CreateClientIdParameter(IDbCommand command, string clientId)
        {           
            return command.CreateParameter(ParameterDirection.Input, "clientId", clientId);
        }


        private static IDbDataParameter CreateIdParameter(IDbCommand command, string id)
        {            
            return command.CreateParameter(ParameterDirection.Input, "id", id);
        }


        private static IDbDataParameter CreateReminderName(IDbCommand command, string reminderName)
        {            
            return command.CreateParameter(ParameterDirection.Input, "reminderName", reminderName);
        }


        private static IDbDataParameter CreateStatusParameter(IDbCommand command, SiloStatus status)
        {            
            return command.CreateParameter(ParameterDirection.Input, "status", (int)status);
        }


        private static IDbDataParameter CreateAddressParameter(IDbCommand command, IPAddress address)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "address";
            parameter.Value = address.ToString();
            parameter.DbType = DbType.AnsiString;
            parameter.Direction = ParameterDirection.Input;

            return parameter;
        }

        private static IDbDataParameter CreateGatewayAddressParameter(IDbCommand command, IPAddress gatewayAddress)
        {            
            string transformedGatewayAddress = gatewayAddress != null ? gatewayAddress.ToString() : null;
            return command.CreateParameter(ParameterDirection.Input, "gatewayAddress", transformedGatewayAddress);
        }


        private static IDbDataParameter CreatePortParameter(IDbCommand command, int port)
        {
            return command.CreateParameter(ParameterDirection.Input, "port", port);
        }


        private static IDbDataParameter CreateGatewayPortParameter(IDbCommand command, int gatewayPort)
        {            
            return command.CreateParameter(ParameterDirection.Input, "gatewayPort", gatewayPort);
        }


        private static IDbDataParameter CreateGenerationParameter(IDbCommand command, int generation)
        {            
            return command.CreateParameter(ParameterDirection.Input, "generation", generation);
        }


        private static IDbDataParameter CreateVersionParameter(IDbCommand command, long version)
        {            
            return command.CreateParameter(ParameterDirection.Input, "version", version);
        }


        private IDbDataParameter CreateEtagParameter(IDbCommand command, string etag)
        {
            var etagBytes = Convert.FromBase64String(etag);
            return command.CreateParameter(ParameterDirection.Input, "etag", etagBytes, etagBytes.Length);
        }


        private IDbDataParameter CreateVersionEtagParameter(IDbCommand command, string versionEtag)
        {
            var etagBytes = Convert.FromBase64String(versionEtag);
            return command.CreateParameter(ParameterDirection.Input, "versionEtag", etagBytes, etagBytes.Length);
        }


        private static IDbDataParameter CreateHostNameParameter(IDbCommand command, string hostName)
        {            
            return command.CreateParameter(ParameterDirection.Input, "hostName", hostName);
        }


        private static IDbDataParameter CreateNameParameter(IDbCommand command, string name)
        {            
            return command.CreateParameter(ParameterDirection.Input, "name", name);
        }

                
        private static IDbDataParameter CreateIsDeltaParameter(IDbCommand command, bool isDelta, int countOf)
        {
            return command.CreateParameter(ParameterDirection.Input, string.Format("isDelta{0}", countOf), isDelta);
        }


        private static IDbDataParameter CreateStatParameter(IDbCommand command, string statValue, int countOf)
        {            
            return command.CreateParameter(ParameterDirection.Input, string.Format("statValue{0}", countOf), statValue);
        }


        private static IDbDataParameter CreateStatisticNameParameter(IDbCommand command, string counterName, int countOf)
        {            
            return command.CreateParameter(ParameterDirection.Input, string.Format("statistic{0}", countOf), counterName);
        }


        private static IDbDataParameter CreateProxyPortParameter(IDbCommand command, int proxyPort)
        {            
            return command.CreateParameter(ParameterDirection.Input, "proxyPort", proxyPort);
        }


        private static IDbDataParameter CreateRoleNameParameter(IDbCommand command, string roleName)
        {
            return command.CreateParameter(ParameterDirection.Input, "roleName", roleName);
        }


        private static IDbDataParameter CreateInstanceNameParameter(IDbCommand command, string instanceName)
        {
            return command.CreateParameter(ParameterDirection.Input, "instanceName", instanceName);
        }


        private static IDbDataParameter CreateUpdateZoneParameter(IDbCommand command, int updateZone)
        {
            return command.CreateParameter(ParameterDirection.Input, "updateZone", updateZone);
        }


        private static IDbDataParameter CreateFaultZoneParameter(IDbCommand command, int faultZone)
        {
            return command.CreateParameter(ParameterDirection.Input, "faultZone", faultZone);
        }


        private static IDbDataParameter CreateStartTimeParameter(IDbCommand command, DateTime startTime)
        {
            return command.CreateParameter(ParameterDirection.Input, "startTime", EnsureSqlMinValue(startTime));
        }


        private static IDbDataParameter CreatePeriodParameter(IDbCommand command, TimeSpan period)
        {
            return command.CreateParameter(ParameterDirection.Input, "period", (int)period.TotalMilliseconds);
        }


        private static IDbDataParameter CreateGrainIdConsistentHashParameter(IDbCommand command, uint grainIdConsistentHash)
        {
            return command.CreateParameter(ParameterDirection.Input, "grainIdConsistentHash", (int)grainIdConsistentHash);
        }


        private static IDbDataParameter CreateIAmAliveTimeParameter(IDbCommand command, DateTime iAmAliveTime)
        {
            return command.CreateParameter(ParameterDirection.Input, "iAmAliveTime", iAmAliveTime);
        }


        private static IDbDataParameter CreateCpuUsageParameter(IDbCommand command, float cpuUsage)
        {
            return command.CreateParameter(ParameterDirection.Input, "cpuUsage", cpuUsage);
        }


        private static IDbDataParameter CreateMemoryUsageParameter(IDbCommand command, long memoryUsage)
        {
            return command.CreateParameter(ParameterDirection.Input, "memoryUsage", memoryUsage);
        }


        private static IDbDataParameter CreateActivationsCountParameter(IDbCommand command, int activationsCount)
        {
            return command.CreateParameter(ParameterDirection.Input, "activationsCount", activationsCount);
        }


        private static IDbDataParameter CreateRecentlyUsedActivationsCountParameter(IDbCommand command, int recentlyUsedActivationsCount)
        {
            return command.CreateParameter(ParameterDirection.Input, "recentlyUsedActivationsCount", recentlyUsedActivationsCount);
        }


        private static IDbDataParameter CreateSendQueueUsageParameter(IDbCommand command, int sendQueueLength)
        {
            return command.CreateParameter(ParameterDirection.Input, "sendQueueLength", sendQueueLength);
        }


        private static IDbDataParameter CreateReceiveQueueLengthParameter(IDbCommand command, int receiveQueueLength)
        {
            return command.CreateParameter(ParameterDirection.Input, "receiveQueueLength", receiveQueueLength);
        }


        private static IDbDataParameter CreateSentMessagesCountParameter(IDbCommand command, long sentMessagesCount)
        {
            return command.CreateParameter(ParameterDirection.Input, "sentMessagesCount", sentMessagesCount);
        }


        private static IDbDataParameter CreateReceivedMessagesCountParameter(IDbCommand command, long receivedMessagesCount)
        {
            return command.CreateParameter(ParameterDirection.Input, "receivedMessagesCount", receivedMessagesCount);
        }


        private static IDbDataParameter CreateRequestQueueLengthParameter(IDbCommand command, long requestQueueLength)
        {
            return command.CreateParameter(ParameterDirection.Input, "requestQueueLength", requestQueueLength);
        }


        private static IDbDataParameter CreateIsOverloadedParameter(IDbCommand command, bool isOverloaded)
        {
            return command.CreateParameter(ParameterDirection.Input, "isOverloaded", isOverloaded);
        }


        private static IDbDataParameter CreateClientCountParameter(IDbCommand command, long clientCount)
        {
            return command.CreateParameter(ParameterDirection.Input, "clientCount", clientCount);
        }


        private static IDbDataParameter CreateConnectionGatewayCountParameter(IDbCommand command, long connectedGatewaysCount)
        {
            return command.CreateParameter(ParameterDirection.Input, "connectedGatewaysCount", connectedGatewaysCount);
        }


        private static IDbDataParameter CreateSuspectingSilosParameter(IDbCommand command, MembershipEntry membershipEntry)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "suspectingSilos";
            parameter.DbType = DbType.String;
            parameter.Direction = ParameterDirection.Input;

            if(membershipEntry.SuspectTimes != null)
            {
                var siloList = new StringBuilder();
                bool first = true;
                foreach(var tuple in membershipEntry.SuspectTimes)
                {
                    if(!first)
                    {
                        siloList.Append('|');
                    }
                    siloList.Append(tuple.Item1.ToParsableString());
                    first = false;
                }

                parameter.Value = siloList.ToString();
            }
            else
            {
                parameter.Value = DBNull.Value;
            }

            return parameter;
        }


        private static IDbDataParameter CreateSuspectingTimesParameter(IDbCommand command, MembershipEntry membershipEntry)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "suspectingTimes";
            parameter.DbType = DbType.String;
            parameter.Direction = ParameterDirection.Input;

            if(membershipEntry.SuspectTimes != null)
            {
                var timeList = new StringBuilder();
                bool first = true;
                foreach(var tuple in membershipEntry.SuspectTimes)
                {
                    if(!first)
                    {
                        timeList.Append('|');
                    }
                    timeList.Append(TraceLogger.PrintDate(tuple.Item2));
                    first = false;
                }

                parameter.Value = timeList.ToString();
            }
            else
            {
                parameter.Value = DBNull.Value;
            }

            return parameter;
        }


        private static DateTime EnsureSqlMinValue(DateTime time)
        {
            return time < SqlDateTime.MinValue.Value ? SqlDateTime.MinValue.Value : time;
        }
        
        /// <summary>
        /// this class is populated via reflection
        /// </summary>
#pragma warning disable 0649
        private class OrleansQueries
        {
            /// <summary>
            /// A query template to retrieve gateway URIs.
            /// </summary>        
            public readonly string ActiveGatewaysQueryKey;

            /// <summary>
            /// A query template to retrieve a single row of membership data.
            /// </summary>        
            public readonly string MembershipReadRowKey;

            /// <summary>
            /// A query template to retrieve all membership data.
            /// </summary>        
            public readonly string MembershipReadAllKey;

            /// <summary>
            /// A query template to insert a membership version row.
            /// </summary>
            public readonly string InsertMembershipVersionKey;

            /// <summary>
            /// A query template to update "I Am Alive Time".
            /// </summary>
            public readonly string UpdateIAmAlivetimeKey;

            /// <summary>
            /// A query template to insert a membership row.
            /// </summary>
            public readonly string InsertMembershipKey;

            /// <summary>
            /// A query template to update a membership row.
            /// </summary>
            public readonly string UpdateMembershipKey;

            /// <summary>
            /// A query template to delete membersip entries.
            /// </summary>
            public readonly string DeleteMembershipTableEntriesKey;

            /// <summary>
            /// A query template to read reminder entries.
            /// </summary>
            public readonly string ReadReminderRowsKey;

            /// <summary>
            /// A query template to read reminder entries with ranges.
            /// </summary>
            public readonly string ReadRangeRows1Key;

            /// <summary>
            /// A query template to read reminder entries with ranges.
            /// </summary>
            public readonly string ReadRangeRows2Key;

            /// <summary>
            /// A query template to read a reminder entry with ranges.
            /// </summary>
            public readonly string ReadReminderRowKey;

            /// <summary>
            /// A query template to upsert a reminder row.
            /// </summary>
            public readonly string UpsertReminderRowKey;

            /// <summary>
            /// A query template to insert Orleans statistics.
            /// </summary>
            public readonly string InsertOrleansStatisticsKey;

            /// <summary>
            /// A query template to insert or update an Orleans client metrics key.
            /// </summary>
            public readonly string UpsertReportClientMetricsKey;

            /// <summary>
            /// A query template to insert or update an Orleans silo metrics key.
            /// </summary>
            public readonly string UpsertSiloMetricsKey;

            /// <summary>
            /// A query template to delete a reminder row.
            /// </summary>
            public readonly string DeleteReminderRowKey;

            /// <summary>
            /// A query template to delete all reminder rows.
            /// </summary>
            public readonly string DeleteReminderRowsKey;
        }
        #pragma warning restore 0649
    }
}
