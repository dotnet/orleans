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

using Orleans.Runtime.Configuration;


namespace Orleans.Runtime.ReminderService
{
    internal static class ReminderTable
    {
        internal static IReminderTable Singleton { get; private set; }

        public static async Task Initialize(Silo silo)
        {
            var config = silo.GlobalConfig;
            var serviceType = config.ReminderServiceType;
            switch (serviceType)
            {
                default:
                    throw new NotSupportedException(
                        String.Format(
                            "The reminder table does not currently support service provider {0}.",
                            serviceType));

                case GlobalConfiguration.ReminderServiceProviderType.SqlServer:
                    Singleton = new SqlReminderTable(config);
                    return;

                case GlobalConfiguration.ReminderServiceProviderType.AzureTable:
                    Singleton = await AzureBasedReminderTable.GetAzureBasedReminderTable(
                            config.ServiceId,
                            config.DeploymentId,
                            config.DataConnectionString);
                    return;

                case GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain:
                    Singleton = GrainFactory.GetGrain<IReminderTable>(Constants.ReminderTableGrainId);
                    return;

                case GlobalConfiguration.ReminderServiceProviderType.MockTable:
                    Singleton = new MockReminderTable(config.MockReminderTableTimeout);
                    return;
            }
        }

        public static Task TestOnlyClearTable()
        {
            return Singleton.TestOnlyClearTable();
        }
    }
}
