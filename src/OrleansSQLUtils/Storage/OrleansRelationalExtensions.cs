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
    /// Orleans specific, hand-crafted convenience queries for efficiency.
    /// </summary>
    /// <remarks>This is public only to be usable to the statistics providers. Not intended for public use otherwise.</remarks>
    public static class OrleansRelationalExtensions
    {
        /// <summary>
        /// When inserting statistics and generating a batch insert clause, these are the columns in the statistics
        /// table that will be updated with multiple values. The other ones are updated with one value only.
        /// </summary>
        private readonly static string[] InsertStatisticsMultiupdateColumns = new[] { "@isDelta", "@statValue", "@statistic" };
        
        
        /// <summary>
        /// Either inserts or updates a silo metrics row.
        /// </summary>
        /// <param name="storage">The storage to use.</param>
        /// <param name="query">The query to use.</param>
        /// <param name="deploymentId">The deployment ID.</param>
        /// <param name="siloId">The silo ID.</param>
        /// <param name="gateway">The gateway information.</param>
        /// <param name="siloAddress">The silo address information.</param>
        /// <param name="hostName">The hostname.</param>
        /// <param name="siloMetrics">The silo metrics to be either updated or inserted.</param>
        /// <returns></returns>
        public static async Task UpsertSiloMetricsAsync(this IRelationalStorage storage, string query, string deploymentId, string siloId, IPEndPoint gateway, SiloAddress siloAddress, string hostName, ISiloPerformanceMetrics siloMetrics)
        {            
            await storage.ExecuteAsync(query, command =>
            {               
                var direction = ParameterDirection.Input;
                var deploymentIdParameter = CreateDeploymentIdParameter(command, deploymentId, direction);
                command.Parameters.Add(deploymentIdParameter);

                var clientIdParameter = CreateSiloIdParameter(command, siloId, direction);
                command.Parameters.Add(clientIdParameter);

                var addressParameter = CreateAddressParameter(command, siloAddress.Endpoint.Address, direction);
                command.Parameters.Add(addressParameter);

                var portParameter = CreatePortParameter(command, siloAddress.Endpoint.Port, direction);
                command.Parameters.Add(portParameter);

                var generationParameter = CreateGenerationParameter(command, siloAddress.Generation, direction);
                command.Parameters.Add(generationParameter);

                var hostNameParameter = CreateHostNameParameter(command, hostName, direction);
                command.Parameters.Add(hostNameParameter);
                
                var gatewayAddressParameter = CreateGatewayAddressParameter(command, gateway != null ? gateway.Address : null, direction);
                command.Parameters.Add(gatewayAddressParameter);

                var gatewayPortParameter = CreateGatewayPortParameter(command, gateway != null ? gateway.Port : 0, direction);
                command.Parameters.Add(gatewayPortParameter);
                
                var cpuUsageParameter = CreateCpuUsageParameter(command, siloMetrics.CpuUsage, direction);
                command.Parameters.Add(cpuUsageParameter);

                var memoryUsageParameter = CreateMemoryUsageParameter(command, siloMetrics.MemoryUsage, direction);
                command.Parameters.Add(memoryUsageParameter);

                var activationsCountParameter = CreateActivationsCountParameter(command, siloMetrics.ActivationCount, direction);
                command.Parameters.Add(activationsCountParameter);

                var recentlyUsedActivationsCountParameter = CreateRecentlyUsedActivationsCountParameter(command, siloMetrics.RecentlyUsedActivationCount, direction);
                command.Parameters.Add(recentlyUsedActivationsCountParameter);

                var sendQueueLengthParameter = CreateSendQueueUsageParameter(command, siloMetrics.SendQueueLength, direction);
                command.Parameters.Add(sendQueueLengthParameter);

                var receiveQueueParameter = CreateReceiveQueueLengthParameter(command, siloMetrics.ReceiveQueueLength, direction);
                command.Parameters.Add(receiveQueueParameter);

                var sentMessagesCountParameter = CreateSentMessagesCountParameter(command, siloMetrics.SentMessages, direction);
                command.Parameters.Add(sentMessagesCountParameter);

                var receivedMessagesCountParameter = CreateReceivedMessagesCountParameter(command, siloMetrics.ReceivedMessages, direction);
                command.Parameters.Add(receivedMessagesCountParameter);

                var requestQueueLengthParameter = CreateRequestQueueLengthParameter(command, siloMetrics.RequestQueueLength, direction);
                command.Parameters.Add(requestQueueLengthParameter);
                
                var isOverloadedParameter = CreateIsOverloadedParameter(command, siloMetrics.IsOverloaded, direction);
                command.Parameters.Add(isOverloadedParameter);

                var clientCountParameter = CreateClientCountParameter(command, siloMetrics.ClientCount, direction);
                command.Parameters.Add(clientCountParameter);
            }).ConfigureAwait(continueOnCapturedContext: false);
        }


        /// <summary>
        /// Either inserts or updates a silo metrics row. 
        /// </summary>
        /// <param name="storage">The storage to use.</param>
        /// <param name="query">The query to use.</param>
        /// <param name="deploymentId">The deployment ID.</param>
        /// <param name="clientId">The client ID.</param>
        /// <param name="clientAddress">The client address information.</param>
        /// <param name="hostName">The hostname.</param>
        /// <param name="clientMetrics">The client metrics to be either updated or inserted.</param>
        /// <returns></returns>
        public static async Task UpsertReportClientMetricsAsync(this IRelationalStorage storage, string query, string deploymentId, string clientId, IPAddress clientAddress, string hostName, IClientPerformanceMetrics clientMetrics)
        {            
            await storage.ExecuteAsync(query, command =>
            {
                var direction = ParameterDirection.Input;
                var deploymentIdParameter = CreateDeploymentIdParameter(command, deploymentId, direction);
                command.Parameters.Add(deploymentIdParameter);

                var clientIdParameter = CreateClientIdParameter(command, clientId, direction);
                command.Parameters.Add(clientIdParameter);

                var addressParameter = CreateAddressParameter(command, clientAddress, direction);
                command.Parameters.Add(addressParameter);

                var hostNameParameter = CreateHostNameParameter(command, hostName, direction);
                command.Parameters.Add(hostNameParameter);

                var cpuUsageParameter = CreateCpuUsageParameter(command, clientMetrics.CpuUsage, direction);
                command.Parameters.Add(cpuUsageParameter);

                var memoryUsageParameter = CreateMemoryUsageParameter(command, clientMetrics.MemoryUsage, direction);
                command.Parameters.Add(memoryUsageParameter);

                var sendQueueLengthParameter = CreateSendQueueUsageParameter(command, clientMetrics.SendQueueLength, direction);
                command.Parameters.Add(sendQueueLengthParameter);

                var receiveQueueParameter = CreateReceiveQueueLengthParameter(command, clientMetrics.ReceiveQueueLength, direction);
                command.Parameters.Add(receiveQueueParameter);

                var sentMessagesCountParameter = CreateSentMessagesCountParameter(command, clientMetrics.SentMessages, direction);
                command.Parameters.Add(sentMessagesCountParameter);

                var receivedMessagesCountParameter = CreateReceivedMessagesCountParameter(command, clientMetrics.ReceivedMessages, direction);
                command.Parameters.Add(receivedMessagesCountParameter);

                var connectionGatewayCountParameter = CreateConnectionGatewayCountParameter(command, clientMetrics.ConnectedGatewayCount, direction);
                command.Parameters.Add(connectionGatewayCountParameter);
            }).ConfigureAwait(continueOnCapturedContext: false);
        }


        /// <summary>
        /// Inserts the given statistics counters to the Orleans database.
        /// </summary>
        /// <param name="storage">The storage to use.</param>
        /// <param name="queryTemplate">The query to use.</param>
        /// <param name="deploymentId">The deployment ID.</param>
        /// <param name="hostName">The hostname.</param>
        /// <param name="siloOrClientName">The silo or client name.</param>
        /// <param name="id">The silo address or client ID.</param>
        /// <param name="counters">The counters to be inserted.</param>        
        public static async Task InsertStatisticsCountersAsync(this IRelationalStorage storage, string queryTemplate, string deploymentId, string hostName, string siloOrClientName, string id, IEnumerable<ICounter> counters)
        {           
            //Zero statistic values mean either that the system is not running or no updates. Such values are not inserted and pruned
            //here so that no insert query or parameters are generated.
            var countersList = counters.Where(i => !"0".Equals(i.IsValueDelta ? i.GetDeltaString() : i.GetValueString())).ToList();

            //Note that the following is almost the same as RelationalStorageExtensions.ExecuteMultipleInsertIntoAsync
            //the only difference being that some columns are skipped. Likely it would be beneficial to introduce
            //a "skip list" to RelationalStorageExtensions.ExecuteMultipleInsertIntoAsync.

            //The template contains an insert for online. The part after SELECT is copied
            //out so that certain parameters can be multiplied by their count. Effectively
            //this turns a query of type (transaction details vary by vendor)
            //BEGIN TRANSACTINION; INSERT INTO [OrleansStatisticsTable] <columns> SELECT <variables>; COMMIT TRANSACTION;
            //to BEGIN TRANSACTINION; INSERT INTO [OrleansStatisticsTable] <columns> SELECT <variables>; UNION ALL <variables> COMMIT TRANSACTION;
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

            //If this is an Oracle database, every UNION ALL SELECT needs to have "FROM DUAL" appended.
            if(storage.InvariantName == AdoNetInvariants.InvariantNameOracleDatabase)
            {
                //Counting starts from 1 as the first SELECT should not select from dual.
                for(int i = 1; i < collectionOfParametersToBeUnionized.Count; ++i)
                {
                    collectionOfParametersToBeUnionized[i] = string.Concat(collectionOfParametersToBeUnionized[i], " FROM DUAL");
                }
            }

            var query = queryTemplate.Replace(template, string.Join(" UNION ALL SELECT ", collectionOfParametersToBeUnionized));
            await storage.ExecuteAsync(query, command =>
            {
                var direction = ParameterDirection.Input;
                var deploymentIdParameter = CreateDeploymentIdParameter(command, deploymentId, direction);
                command.Parameters.Add(deploymentIdParameter);

                var idParameter = CreateIdParameter(command, id, direction);
                command.Parameters.Add(idParameter);

                var hostNameParameter = CreateHostNameParameter(command, hostName, direction);
                command.Parameters.Add(hostNameParameter);

                var nameParameter = CreateNameParameter(command, siloOrClientName, direction);
                command.Parameters.Add(nameParameter);

                for(int i = 0; i < countersList.Count; ++i)
                {
                    var isDeltaParameter = CreateIsDeltaParameter(command, countersList[i].IsValueDelta, i, direction);
                    command.Parameters.Add(isDeltaParameter);

                    var statParameter = CreateStatParameter(command, countersList[i].IsValueDelta ? countersList[i].GetDeltaString() : countersList[i].GetValueString(), i, direction);
                    command.Parameters.Add(statParameter);

                    var statisticNameParameter = CreateStatisticNameParameter(command, countersList[i].Name, i, direction);
                    command.Parameters.Add(statisticNameParameter);
                }
            }).ConfigureAwait(continueOnCapturedContext: false);
        }


        /// <summary>
        /// Reads Orleans reminder data from the tables.
        /// </summary>
        /// <param name="storage">The storage to use.</param>
        /// <param name="query">The query to use.</param>
        /// <param name="serviceId">The service ID.</param>
        /// <param name="grainRef">The grain reference (ID).</param>
        /// <returns>Reminder table data.</returns>
        internal static async Task<ReminderTableData> ReadReminderRowsAsync(this IRelationalStorage storage, string query, string serviceId, GrainReference grainRef)
        {            
            var ret = await storage.ReadAsync(query, command =>
            {
                var direction = ParameterDirection.Input;
                var serviceIdParameter = CreateServiceIdParameter(command, serviceId, direction);
                command.Parameters.Add(serviceIdParameter);

                var grainIdParameter = CreateGrainIdParameter(command, grainRef.ToKeyString(), direction);
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
        /// <param name="storage">The storage to use.</param>
        /// <param name="query">The query to use.</param>
        /// <param name="serviceId">The service ID.</param>
        /// <param name="beginHash">The begin hash.</param>
        /// <param name="endHash">The end hash.</param>
        /// <returns>Reminder table data.</returns>
        internal static async Task<ReminderTableData> ReadReminderRowsAsync(this IRelationalStorage storage, string query, string serviceId, uint beginHash, uint endHash)
        {            
            var ret = await storage.ReadAsync(query, command =>
            {
                var direction = ParameterDirection.Input;
                var serviceIdParameter = CreateServiceIdParameter(command, serviceId, direction);
                command.Parameters.Add(serviceIdParameter);

                var beginHashParameter = CreateBeginHashParameter(command, beginHash, direction);
                command.Parameters.Add(beginHashParameter);

                var endHashParameter = CreateEndHashParameter(command, endHash, direction);
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
        /// <param name="storage">The storage to use.</param>
        /// <param name="query">The query to use.</param>
        /// <param name="serviceId">Service ID.</param>
        /// <param name="grainRef">The grain reference (ID).</param>
        /// <param name="reminderName">The reminder name to retrieve.</param>
        /// <returns>A remainder entry.</returns>
        internal static async Task<ReminderEntry> ReadReminderRowAsync(this IRelationalStorage storage, string query, string serviceId, GrainReference grainRef, string reminderName)
        {            
            var ret = await storage.ReadAsync(query, command =>
            {
                var direction = ParameterDirection.Input;
                var serviceIdParameter = CreateServiceIdParameter(command, serviceId, direction);
                command.Parameters.Add(serviceIdParameter);

                var grainIdParameter = CreateGrainIdParameter(command, grainRef.ToKeyString(), direction);
                command.Parameters.Add(grainIdParameter);

                var reminderNameParameter = CreateReminderName(command, reminderName, direction);
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
        /// <param name="storage">The storage to use.</param>
        /// <param name="query">The query to use.</param>
        /// <param name="serviceId">The service ID.</param>
        /// <param name="grainRef">The grain reference (ID).</param>
        /// <param name="reminderName">The reminder name to retrieve.</param>
        /// <param name="startTime">Start time of the reminder.</param>
        /// <param name="period">Period of the reminder.</param>
        /// <returns>The new etag of the either or updated or inserted reminder row.</returns>
        internal static async Task<string> UpsertReminderRowAsync(this IRelationalStorage storage, string query, string serviceId, GrainReference grainRef, string reminderName, DateTime startTime, TimeSpan period)
        {            
            var ret = await storage.ReadAsync(query, command =>
            {
                var direction = ParameterDirection.Input;
                var serviceIdParameter = CreateServiceIdParameter(command, serviceId, direction);
                command.Parameters.Add(serviceIdParameter);

                var grainIdParameter = CreateGrainIdParameter(command, grainRef.ToKeyString(), direction);
                command.Parameters.Add(grainIdParameter);

                var reminderNameParameter = CreateReminderName(command, reminderName, direction);
                command.Parameters.Add(reminderNameParameter);

                var startTimeParameter = CreateStartTimeParameter(command, startTime, direction);
                command.Parameters.Add(startTimeParameter);

                var periodParameter = CreatePeriodParameter(command, period, direction);
                command.Parameters.Add(periodParameter);

                var grainIdConsistentHashParameter = CreateGrainIdConsistentHashParameter(command, grainRef.GetUniformHashCode(), direction);
                command.Parameters.Add(grainIdConsistentHashParameter);
            }, (selector, _) =>
            {
                return Convert.ToBase64String(selector.GetValueOrDefault<byte[]>("ETag"));
            }).ConfigureAwait(continueOnCapturedContext: false);

            return ret != null ? ret.FirstOrDefault() : null;
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="storage"></param>
        /// <param name="query">The query to use.</param>
        /// <param name="serviceId">Service ID.</param>
        /// <param name="grainRef"></param>
        /// <param name="reminderName"></param>
        /// <param name="etag"></param>
        /// <returns></returns>
        internal static async Task<bool> DeleteReminderRowAsync(this IRelationalStorage storage, string query, string serviceId, GrainReference grainRef, string reminderName, string etag)
        {            
            var ret = await storage.ReadAsync(query, command =>
            {
                var direction = ParameterDirection.Input;
                var serviceIdParameter = CreateServiceIdParameter(command, serviceId, direction);
                command.Parameters.Add(serviceIdParameter);

                var grainIdParameter = CreateGrainIdParameter(command, grainRef.ToKeyString(), direction);
                command.Parameters.Add(grainIdParameter);

                var reminderNameParameter = CreateReminderName(command, reminderName, direction);
                command.Parameters.Add(reminderNameParameter);

                var etagParameter = CreateEtagParameter(command, etag, direction);
                command.Parameters.Add(etagParameter);
            }, (selector, _) =>
            {
                return selector.GetBoolean(0);
            }).ConfigureAwait(continueOnCapturedContext: false);

            return ret.First();
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="storage"></param>
        /// <param name="query">The query to use.</param>
        /// <param name="serviceId"></param>
        /// <returns></returns>
        internal static async Task DeleteReminderRowsAsync(this IRelationalStorage storage, string query, string serviceId)
        {            
            await storage.ExecuteAsync(query, command =>
            {
                var direction = ParameterDirection.Input;
                var serviceIdParameter = CreateServiceIdParameter(command, serviceId, direction);
                command.Parameters.Add(serviceIdParameter);
            }).ConfigureAwait(continueOnCapturedContext: false);
        }



        /// <summary>
        /// Lists active gateways. Used mainly by Orleans clients.
        /// </summary>
        /// <param name="storage">The storage to use.</param>
        /// <param name="query">The query to use.</param>
        /// <param name="deploymentId">The deployment for which to query the gateways.</param>
        /// <returns>The gateways for the silo.</returns>
        internal static async Task<IList<Uri>> ActiveGatewaysAsync(this IRelationalStorage storage, string query, string deploymentId)
        {            
            var ret = await storage.ReadAsync(query, command =>
            {
                var direction = ParameterDirection.Input;
                var siloIdParameter = CreateDeploymentIdParameter(command, deploymentId, direction);
                command.Parameters.Add(siloIdParameter);

                var statusParameter = CreateStatusParameter(command, SiloStatus.Active, direction);
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
        /// <param name="storage">The storage to use.</param>
        /// <param name="query">The query to use.</param>
        /// <param name="deploymentId">The deployment for which to query data.</param>
        /// <param name="key">Silo data used as parameters in the query.</param>
        /// <returns>Membership table data.</returns>
        internal static async Task<MembershipTableData> MembershipDataAsync(this IRelationalStorage storage, string query, string deploymentId, SiloAddress key)
        {            
            var ret = await storage.ReadAsync(query, command =>
            {
                var direction = ParameterDirection.Input;
                var siloIdParameter = CreateDeploymentIdParameter(command, deploymentId, direction);
                command.Parameters.Add(siloIdParameter);

                var addressParameter = CreateAddressParameter(command, key.Endpoint.Address, direction);
                command.Parameters.Add(addressParameter);

                var portParameter = CreatePortParameter(command, key.Endpoint.Port, direction);
                command.Parameters.Add(portParameter);

                var generationParameter = CreateGenerationParameter(command, key.Generation, direction);
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
        /// 
        /// </summary>
        /// <param name="storage"></param>
        /// <param name="query">The query to use.</param>
        /// <param name="deploymentId"></param>
        /// <returns></returns>
        internal static async Task<MembershipTableData> AllMembershipDataAsync(this IRelationalStorage storage, string query, string deploymentId)
        {            
            var ret = await storage.ReadAsync(query, command =>
            {
                var direction = ParameterDirection.Input;
                var deploymentIdParameter = CreateDeploymentIdParameter(command, deploymentId, direction);
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
        /// 
        /// </summary>
        /// <param name="storage"></param>
        /// <param name="query">The query to use.</param>
        /// <param name="deploymentId"></param>
        /// <returns></returns>
        internal static async Task DeleteMembershipTableEntriesAsync(this IRelationalStorage storage, string query, string deploymentId)
        {            
            await storage.ExecuteAsync(query, command =>
            {
                var direction = ParameterDirection.Input;
                var siloIdParameter = CreateDeploymentIdParameter(command, deploymentId, direction);
                command.Parameters.Add(siloIdParameter);
            }).ConfigureAwait(continueOnCapturedContext: false);
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="storage"></param>
        /// <param name="query">The query to use.</param>
        /// <param name="deploymentId"></param>
        /// <param name="membershipEntry"></param>
        /// <returns></returns>
        internal static async Task UpdateIAmAliveTimeAsync(this IRelationalStorage storage, string query, string deploymentId, MembershipEntry membershipEntry)
        {            
            await storage.ExecuteAsync(query, command =>
            {
                var direction = ParameterDirection.Input;
                var siloIdParameter = CreateDeploymentIdParameter(command, deploymentId, direction);
                command.Parameters.Add(siloIdParameter);

                var iAmAliveTimeParameter = CreateIAmAliveTimeParameter(command, membershipEntry.IAmAliveTime, direction);
                command.Parameters.Add(iAmAliveTimeParameter);

                var addressParameter = CreateAddressParameter(command, membershipEntry.SiloAddress.Endpoint.Address, direction);
                command.Parameters.Add(addressParameter);

                var portParameter = CreatePortParameter(command, membershipEntry.SiloAddress.Endpoint.Port, direction);
                command.Parameters.Add(portParameter);

                var generationParameter = CreateGenerationParameter(command, membershipEntry.SiloAddress.Generation, direction);
                command.Parameters.Add(generationParameter);
            }).ConfigureAwait(continueOnCapturedContext: false);
        }


        /// <summary>
        /// Inserts a version row if one does not already exist.
        /// </summary>
        /// <param name="storage">The storage to use.</param>
        /// <param name="query">The query to use.</param>
        /// <param name="deploymentId">The deployment for which to query data.</param>
        /// <param name="version">The version information to insert.</param>
        /// <returns><em>TRUE</em> if a row was inserted. <em>FALSE</em> otherwise.</returns>
        internal static async Task<bool> InsertMembershipVersionRowAsync(this IRelationalStorage storage, string query, string deploymentId, int version)
        {            
            var ret = await storage.ReadAsync(query, command =>
            {
                var direction = ParameterDirection.Input;
                var siloIdParameter = CreateDeploymentIdParameter(command, deploymentId, direction);
                command.Parameters.Add(siloIdParameter);

                var versionParameter = CreateVersionParameter(command, version, direction);
                command.Parameters.Add(versionParameter);
            }, (selector, _) => { return selector.GetBoolean(0); }).ConfigureAwait(continueOnCapturedContext: false);

            return ret.First();
        }


        /// <summary>
        /// Inserts a membership row if one does not already exist.
        /// </summary>
        /// <param name="storage">The storage to use.</param>
        /// <param name="query">The query to use.</param>
        /// <param name="deploymentId">The deployment with which to insert row.</param>
        /// <param name="membershipEntry">The membership entry data to insert.</param>
        /// <param name="version">The version data to insert.</param>
        /// <returns><em>TRUE</em> if insert succeeds. <em>FALSE</em> otherwise.</returns>
        internal static async Task<bool> InsertMembershipRowAsync(this IRelationalStorage storage, string query, string deploymentId, MembershipEntry membershipEntry, TableVersion version)
        {            
            var ret = await storage.ReadAsync(query, command =>
            {
                var direction = ParameterDirection.Input;
                var siloIdParameter = CreateDeploymentIdParameter(command, deploymentId, direction);
                command.Parameters.Add(siloIdParameter);

                var versionParameter = CreateVersionParameter(command, version.Version, direction);
                command.Parameters.Add(versionParameter);

                var versionEtagParameter = CreateVersionEtagParameter(command, version.VersionEtag, direction);
                command.Parameters.Add(versionEtagParameter);

                //The insert membership row part.
                var addressParameter = CreateAddressParameter(command, membershipEntry.SiloAddress.Endpoint.Address, direction);
                command.Parameters.Add(addressParameter);

                var portParameter = CreatePortParameter(command, membershipEntry.SiloAddress.Endpoint.Port, direction);
                command.Parameters.Add(portParameter);

                var generationParameter = CreateGenerationParameter(command, membershipEntry.SiloAddress.Generation, direction);
                command.Parameters.Add(generationParameter);

                var hostNameParameter = CreateHostNameParameter(command, membershipEntry.HostName, direction);
                command.Parameters.Add(hostNameParameter);

                var statusParameter = CreateStatusParameter(command, membershipEntry.Status, direction);
                command.Parameters.Add(statusParameter);

                var proxyPortParameter = CreateProxyPortParameter(command, membershipEntry.ProxyPort, direction);
                command.Parameters.Add(proxyPortParameter);

                var roleNameParameter = CreateRoleNameParameter(command, membershipEntry.RoleName, direction);
                command.Parameters.Add(roleNameParameter);

                var instanceNameParameter = CreateInstanceNameParameter(command, membershipEntry.InstanceName, direction);
                command.Parameters.Add(instanceNameParameter);

                var updateZoneParameter = CreateUpdateZoneParameter(command, membershipEntry.UpdateZone, direction);
                command.Parameters.Add(updateZoneParameter);

                var faultZoneParameter = CreateFaultZoneParameter(command, membershipEntry.FaultZone, direction);
                command.Parameters.Add(faultZoneParameter);

                var startTimeParameter = CreateStartTimeParameter(command, membershipEntry.StartTime, direction);
                command.Parameters.Add(startTimeParameter);

                var iAmAliveTimeParameter = CreateIAmAliveTimeParameter(command, membershipEntry.IAmAliveTime, direction);
                command.Parameters.Add(iAmAliveTimeParameter);

                var suspectingSilosParameter = CreateSuspectingSilosParameter(command, membershipEntry, direction);
                command.Parameters.Add(suspectingSilosParameter);

                var suspectingTimesParameter = CreateSuspectingTimesParameter(command, membershipEntry, direction);
                command.Parameters.Add(suspectingTimesParameter);

            }, (selector, _) => { return selector.GetBoolean(0); }).ConfigureAwait(continueOnCapturedContext: false);

            return ret.First();
        }


        /// <summary>
        /// Updates membership row data.
        /// </summary>
        /// <param name="storage">The storage to use.</param>
        /// <param name="query">The query to use.</param>
        /// <param name="deploymentId">The deployment with which to insert row.</param>
        /// <param name="etag">The etag of which to use to check if the membership data being updated is not stale.</param>
        /// <param name="membershipEntry">The membership data to used to update database.</param>
        /// <param name="version">The membership version used to update database.</param>
        /// <returns><em>TRUE</em> if update SUCCEEDS. <em>FALSE</em> ot</returns>
        internal static async Task<bool> UpdateMembershipRowAsync(this IRelationalStorage storage, string query, string deploymentId, string etag, MembershipEntry membershipEntry, TableVersion version)
        {            
            var ret = await storage.ReadAsync(query, command =>
            {
                var direction = ParameterDirection.Input;
                var siloIdParameter = CreateDeploymentIdParameter(command, deploymentId, direction);
                command.Parameters.Add(siloIdParameter);

                var versionParameter = CreateVersionParameter(command, version.Version, direction);
                command.Parameters.Add(versionParameter);

                var versionEtagParameter = CreateVersionEtagParameter(command, version.VersionEtag, direction);
                command.Parameters.Add(versionEtagParameter);

                //The insert membership row part.
                var etagParameter = CreateEtagParameter(command, etag, direction);
                command.Parameters.Add(etagParameter);

                var addressParameter = CreateAddressParameter(command, membershipEntry.SiloAddress.Endpoint.Address, direction);
                command.Parameters.Add(addressParameter);

                var portParameter = CreatePortParameter(command, membershipEntry.SiloAddress.Endpoint.Port, direction);
                command.Parameters.Add(portParameter);

                var generationParameter = CreateGenerationParameter(command, membershipEntry.SiloAddress.Generation, direction);
                command.Parameters.Add(generationParameter);

                var hostNameParameter = CreateHostNameParameter(command, membershipEntry.HostName, direction);
                command.Parameters.Add(hostNameParameter);

                var statusParameter = CreateStatusParameter(command, membershipEntry.Status, direction);
                command.Parameters.Add(statusParameter);

                var proxyPortParameter = CreateProxyPortParameter(command, membershipEntry.ProxyPort, direction);
                command.Parameters.Add(proxyPortParameter);

                var roleNameParameter = CreateRoleNameParameter(command, membershipEntry.RoleName, direction);
                command.Parameters.Add(roleNameParameter);

                var instanceNameParameter = CreateInstanceNameParameter(command, membershipEntry.InstanceName, direction);
                command.Parameters.Add(instanceNameParameter);

                var updateZoneParameter = CreateUpdateZoneParameter(command, membershipEntry.UpdateZone, direction);
                command.Parameters.Add(updateZoneParameter);

                var faultZoneParameter = CreateFaultZoneParameter(command, membershipEntry.FaultZone, direction);
                command.Parameters.Add(faultZoneParameter);

                var startTimeParameter = CreateStartTimeParameter(command, membershipEntry.StartTime, direction);
                command.Parameters.Add(startTimeParameter);

                var iAmAliveTimeParameter = CreateIAmAliveTimeParameter(command, membershipEntry.IAmAliveTime, direction);
                command.Parameters.Add(iAmAliveTimeParameter);

                var suspectingSilosParameter = CreateSuspectingSilosParameter(command, membershipEntry, direction);
                command.Parameters.Add(suspectingSilosParameter);

                var suspectingTimesParameter = CreateSuspectingTimesParameter(command, membershipEntry, direction);
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


        private static Tuple<MembershipEntry, string, int, string> CreateMembershipEntry(IDataRecord record)
        {
            //TODO: This is a bit of hack way to check in the current version if there's membership data or not, but if there's a start time, there's member.            
            DateTime? startTime = record.GetValueOrDefault<DateTime?>("StartTime");                                    
            MembershipEntry entry = null;
            if(startTime.HasValue)
            {
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

            string etag = Convert.ToBase64String(record.GetValue<byte[]>("ETag"));
            int tableVersion = (int)record.GetValueOrDefault<long>("Version");
            string versionETag = Convert.ToBase64String(record.GetValueOrDefault<byte[]>("VersionETag"));

            return Tuple.Create(entry, etag, tableVersion, versionETag);
        }


        private static IDbDataParameter CreateBeginHashParameter(IDbCommand command, uint beginHash, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "beginHash";
            parameter.Value = (int)beginHash;
            parameter.DbType = DbType.Int32;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateEndHashParameter(IDbCommand command, uint endHash, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "endHash";
            parameter.Value = (int)endHash;
            parameter.DbType = DbType.Int32;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateServiceIdParameter(IDbCommand command, string serviceId, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "serviceId";
            parameter.Value = serviceId;
            parameter.DbType = DbType.String;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateGrainIdParameter(IDbCommand command, string grainId, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "grainId";
            parameter.Value = grainId;
            parameter.DbType = DbType.String;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateDeploymentIdParameter(IDbCommand command, string deploymentId, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "deploymentId";
            parameter.Value = deploymentId;
            parameter.DbType = DbType.String;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateSiloIdParameter(IDbCommand command, string siloId, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "siloId";
            parameter.Value = siloId;
            parameter.DbType = DbType.String;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateClientIdParameter(IDbCommand command, string clientId, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "clientId";
            parameter.Value = clientId;
            parameter.DbType = DbType.String;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateIdParameter(IDbCommand command, string id, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "id";
            parameter.Value = id;
            parameter.DbType = DbType.String;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateReminderName(IDbCommand command, string reminderName, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "reminderName";
            parameter.Value = reminderName;
            parameter.DbType = DbType.String;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateStatusParameter(IDbCommand command, SiloStatus status, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "status";
            parameter.Value = (int)status;
            parameter.DbType = DbType.Int32;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateAddressParameter(IDbCommand command, IPAddress address, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "address";
            parameter.Value = address.ToString();
            parameter.DbType = DbType.AnsiString;
            parameter.Direction = direction;

            return parameter;
        }

        private static IDbDataParameter CreateGatewayAddressParameter(IDbCommand command, IPAddress gatewayAddress, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "gatewayAddress";
            parameter.Value = gatewayAddress != null ? (object)gatewayAddress.ToString() : DBNull.Value;
            parameter.DbType = DbType.AnsiString;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreatePortParameter(IDbCommand command, int port, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "port";
            parameter.Value = port;
            parameter.DbType = DbType.Int32;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateGatewayPortParameter(IDbCommand command, int gatewayPort, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "gatewayPort";
            parameter.Value = gatewayPort;
            parameter.DbType = DbType.Int32;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateGenerationParameter(IDbCommand command, int generation, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "generation";
            parameter.Value = generation;
            parameter.DbType = DbType.Int32;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateVersionParameter(IDbCommand command, Int64 version, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "version";
            parameter.Value = version;
            parameter.DbType = DbType.Int64;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateEtagParameter(IDbCommand command, string etag, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "etag";
            parameter.Value = etag != null ? (object)Convert.FromBase64String(etag) : DBNull.Value;
            parameter.DbType = DbType.Binary;
            parameter.Size = 16;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateVersionEtagParameter(IDbCommand command, string versionEtag, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "versionEtag";
            parameter.Value = Convert.FromBase64String(versionEtag);
            parameter.DbType = DbType.Binary;
            parameter.Size = 16;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateHostNameParameter(IDbCommand command, string hostName, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "hostName";
            parameter.Value = hostName;
            parameter.DbType = DbType.String;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateNameParameter(IDbCommand command, string name, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "name";
            parameter.Value = name;
            parameter.DbType = DbType.String;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateCounterParameter(IDbCommand command, long counter, int countOf, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = string.Format("counter{0}", countOf);
            parameter.Value = counter;
            parameter.DbType = DbType.Int64;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateIsDeltaParameter(IDbCommand command, bool isDelta, int countOf, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = string.Format("isDelta{0}", countOf);
            parameter.Value = isDelta;
            parameter.DbType = DbType.Boolean;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateStatParameter(IDbCommand command, string statValue, int countOf, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = string.Format("statValue{0}", countOf);
            parameter.Value = statValue;
            parameter.DbType = DbType.String;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateStatisticNameParameter(IDbCommand command, string counterName, int countOf, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = string.Format("statistic{0}", countOf);
            parameter.Value = counterName;
            parameter.DbType = DbType.String;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateProxyPortParameter(IDbCommand command, int proxyPort, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "proxyPort";
            parameter.Value = proxyPort;
            parameter.DbType = DbType.Int32;
            parameter.Direction = direction;

            return parameter;
        }

        private static IDbDataParameter CreateRoleNameParameter(IDbCommand command, string roleName, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "roleName";
            parameter.Value = roleName;
            parameter.DbType = DbType.String;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateInstanceNameParameter(IDbCommand command, string instanceName, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "instanceName";
            parameter.Value = instanceName;
            parameter.DbType = DbType.String;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateUpdateZoneParameter(IDbCommand command, int updateZone, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "updateZone";
            parameter.Value = updateZone;
            parameter.DbType = DbType.Int32;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateFaultZoneParameter(IDbCommand command, int faultZone, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "faultZone";
            parameter.Value = faultZone;
            parameter.DbType = DbType.Int32;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateStartTimeParameter(IDbCommand command, DateTime startTime, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "startTime";
            parameter.Value = EnsureSqlMinValue(startTime);
            parameter.DbType = DbType.DateTime;//Using DateTime for cross DB compatibility. The underlying DB table column type can be DateTime or DateTime2
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreatePeriodParameter(IDbCommand command, TimeSpan period, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "period";
            parameter.Value = (int)period.TotalMilliseconds;
            parameter.DbType = DbType.Int32;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateGrainIdConsistentHashParameter(IDbCommand command, uint reminderHash, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "grainIdConsistentHash";
            parameter.Value = (int)reminderHash;
            parameter.DbType = DbType.Int32;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateIAmAliveTimeParameter(IDbCommand command, DateTime iAmAlive, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "iAmAliveTime";
            parameter.Value = EnsureSqlMinValue(iAmAlive);
            parameter.DbType = DbType.DateTime;//Using DateTime for cross DB compatibility. The underlying DB table column type can be DateTime or DateTime2
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateCpuUsageParameter(IDbCommand command, float cpuUsage, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "cpuUsage";
            parameter.Value = cpuUsage;
            parameter.DbType = DbType.Single;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateMemoryUsageParameter(IDbCommand command, long memoryUsage, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "memoryUsage";
            parameter.Value = memoryUsage;
            parameter.DbType = DbType.Int64;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateActivationsCountParameter(IDbCommand command, int activationsCount, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "activationsCount";
            parameter.Value = activationsCount;
            parameter.DbType = DbType.Int32;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateRecentlyUsedActivationsCountParameter(IDbCommand command, int recentlyUsedActivationsCount, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "recentlyUsedActivationsCount";
            parameter.Value = recentlyUsedActivationsCount;
            parameter.DbType = DbType.Int32;
            parameter.Direction = direction;

            return parameter;
        }

        
        private static IDbDataParameter CreateSendQueueUsageParameter(IDbCommand command, int sendQueueLength, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "sendQueueLength";
            parameter.Value = sendQueueLength;
            parameter.DbType = DbType.Int32;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateReceiveQueueLengthParameter(IDbCommand command, int receiveQueueLength, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "receiveQueueLength";
            parameter.Value = receiveQueueLength;
            parameter.DbType = DbType.Int32;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateSentMessagesCountParameter(IDbCommand command, long sentMessages, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "sentMessagesCount";
            parameter.Value = sentMessages;
            parameter.DbType = DbType.Int64;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateReceivedMessagesCountParameter(IDbCommand command, long receivedMessages, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "receivedMessagesCount";
            parameter.Value = receivedMessages;
            parameter.DbType = DbType.Int64;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateRequestQueueLengthParameter(IDbCommand command, long requestQueueLength, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "requestQueueLength";
            parameter.Value = requestQueueLength;
            parameter.DbType = DbType.Int64;
            parameter.Direction = direction;

            return parameter;
        }
                

        private static IDbDataParameter CreateIsOverloadedParameter(IDbCommand command, bool isOverloaded, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "isOverloaded";
            parameter.Value = isOverloaded;
            parameter.DbType = DbType.Boolean;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateClientCountParameter(IDbCommand command, long clientCount, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "clientCount";
            parameter.Value = clientCount;
            parameter.DbType = DbType.Int64;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateConnectionGatewayCountParameter(IDbCommand command, long connectedGatewaysCount, ParameterDirection direction)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "connectedGatewaysCount";
            parameter.Value = connectedGatewaysCount;
            parameter.DbType = DbType.Int32;
            parameter.Direction = direction;

            return parameter;
        }


        private static IDbDataParameter CreateSuspectingSilosParameter(IDbCommand command, MembershipEntry membershipEntry, ParameterDirection direction)
        {            
            var parameter = command.CreateParameter();
            parameter.ParameterName = "suspectingSilos";
            parameter.DbType = DbType.String;
            parameter.Direction = direction;

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


        private static IDbDataParameter CreateSuspectingTimesParameter(IDbCommand command, MembershipEntry membershipEntry, ParameterDirection direction)
        {            
            var parameter = command.CreateParameter();
            parameter.ParameterName = "suspectingTimes";
            parameter.DbType = DbType.String;
            parameter.Direction = direction;

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
    }
}