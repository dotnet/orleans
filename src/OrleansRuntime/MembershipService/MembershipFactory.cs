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
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.MembershipService
{
    internal class MembershipFactory
    {
        private readonly TraceLogger logger;

        internal MembershipFactory()
        {
            logger = TraceLogger.GetLogger("MembershipFactory", TraceLogger.LoggerType.Runtime);
        }

        internal Task CreateMembershipTableProvider(Catalog catalog, Silo silo)
        {
            var livenessType = silo.GlobalConfig.LivenessType;
            logger.Info(ErrorCode.MembershipFactory1, "Creating membership table provider for type={0}", Enum.GetName(typeof(GlobalConfiguration.LivenessProviderType), livenessType));
            if (livenessType.Equals(GlobalConfiguration.LivenessProviderType.MembershipTableGrain))
            {
                return catalog.CreateSystemGrain(
                        Constants.SystemMembershipTableId,
                        typeof(GrainBasedMembershipTable).FullName);
            }
            return TaskDone.Done;
        }

        internal IMembershipOracle CreateMembershipOracle(Silo silo, IMembershipTable membershipTable)
        {
            var livenessType = silo.GlobalConfig.LivenessType;
            logger.Info("Creating membership oracle for type={0}", Enum.GetName(typeof(GlobalConfiguration.LivenessProviderType), livenessType));
            return new MembershipOracle(silo, membershipTable);
        }

        internal IMembershipTable GetMembershipTable(GlobalConfiguration.LivenessProviderType livenessType, string membershipTableAssembly = null)
        {
            IMembershipTable membershipTable;
            if (livenessType.Equals(GlobalConfiguration.LivenessProviderType.MembershipTableGrain))
            {
                membershipTable =
                    GrainReference.FromGrainId(Constants.SystemMembershipTableId).Cast<IMembershipTableGrain>();
            }
            else if (livenessType.Equals(GlobalConfiguration.LivenessProviderType.SqlServer))
            {
                membershipTable = AssemblyLoader.LoadAndCreateInstance<IMembershipTable>(Constants.ORLEANS_SQL_UTILS_DLL, logger);
            }
            else if (livenessType.Equals(GlobalConfiguration.LivenessProviderType.AzureTable))
            {
                membershipTable = AssemblyLoader.LoadAndCreateInstance<IMembershipTable>(Constants.ORLEANS_AZURE_UTILS_DLL, logger);
            }
            else if (livenessType.Equals(GlobalConfiguration.LivenessProviderType.ZooKeeper))
            {
                membershipTable = AssemblyLoader.LoadAndCreateInstance<IMembershipTable>(Constants.ORLEANS_ZOOKEEPER_UTILS_DLL, logger);
            }
            else if (livenessType.Equals(GlobalConfiguration.LivenessProviderType.Custom))
            {
                membershipTable = AssemblyLoader.LoadAndCreateInstance<IMembershipTable>(membershipTableAssembly, logger);
            }
            else
            {
                throw new NotImplementedException("No membership table provider found for LivenessType=" + livenessType);
            }

            return membershipTable;
        }

        // Only used with MembershipTableGrain to wait for primary to start.
        internal async Task WaitForTableToInit(IMembershipTable membershipTable)
        {
            var timespan = Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(5);
            // This is a quick temporary solution to enable primary node to start fully before secondaries.
            // Secondary silos waits untill GrainBasedMembershipTable is created. 
            for (int i = 0; i < 100; i++)
            {
                bool needToWait = false;
                try
                {
                    MembershipTableData table = await membershipTable.ReadAll().WithTimeout(timespan);
                    logger.Info(ErrorCode.MembershipTableGrainInit2, "-Connected to membership table provider.");
                    return;
                }
                catch (Exception exc)
                {
                    var type = exc.GetBaseException().GetType();
                    if (type == typeof(TimeoutException) || type == typeof(OrleansException))
                    {
                        logger.Info(ErrorCode.MembershipTableGrainInit3,
                            "-Waiting for membership table provider to initialize. Going to sleep for {0} and re-try to reconnect.", timespan);
                        needToWait = true;
                    }
                    else
                    {
                        logger.Info(ErrorCode.MembershipTableGrainInit4, "-Membership table provider failed to initialize. Giving up.");
                        throw;
                    }
                }

                if (needToWait)
                {
                    await Task.Delay(timespan);
                }
            }
        }
    }
}
