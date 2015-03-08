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

﻿using System.Collections.Generic;
using System.Diagnostics;
using System;

namespace Orleans.Runtime
{
    internal class Constants
    {
        // This needs to be first, as GrainId static initializers reference it. Otherwise, GrainId actually see a uninitialized (ie Zero) value for that "constant"!
        public static readonly TimeSpan INFINITE_TIMESPAN = TimeSpan.FromMilliseconds(-1);

        // We assume that clock skew between silos and between clients and silos is always less than 1 second
        public static readonly TimeSpan MAXIMUM_CLOCK_SKEW = TimeSpan.FromSeconds(1);

        public const string DEFAULT_STORAGE_PROVIDER_NAME = "Default";
        public const string MEMORY_STORAGE_PROVIDER_NAME = "MemoryStore";
        public const string DATA_CONNECTION_STRING_NAME = "DataConnectionString";

        public static readonly GrainId DirectoryServiceId = GrainId.GetSystemTargetGrainId(10);
        public static readonly GrainId DirectoryCacheValidatorId = GrainId.GetSystemTargetGrainId(11);
        public static readonly GrainId SiloControlId = GrainId.GetSystemTargetGrainId(12);
        public static readonly GrainId ClientObserverRegistrarId = GrainId.GetSystemTargetGrainId(13);
        public static readonly GrainId CatalogId = GrainId.GetSystemTargetGrainId(14);
        public static readonly GrainId MembershipOracleId = GrainId.GetSystemTargetGrainId(15);
        public static readonly GrainId ReminderServiceId = GrainId.GetSystemTargetGrainId(16);
        public static readonly GrainId TypeManagerId = GrainId.GetSystemTargetGrainId(17);
        public static readonly GrainId ProviderManagerSystemTargetId = GrainId.GetSystemTargetGrainId(19);
        public static readonly GrainId DeploymentLoadPublisherSystemTargetId = GrainId.GetSystemTargetGrainId(22);
       
        public const int PULLING_AGENTS_MANAGER_SYSTEM_TARGET_TYPE_CODE = 254;
        public const int PULLING_AGENT_SYSTEM_TARGET_TYPE_CODE = 255;

        public static readonly GrainId SystemMembershipTableId = GrainId.GetSystemGrainId(new Guid("01145FEC-C21E-11E0-9105-D0FB4724019B"));
        public static readonly GrainId SiloDirectConnectionId = GrainId.GetSystemGrainId(new Guid("01111111-1111-1111-1111-111111111111"));

        internal const long ReminderTableGrainId = 12345;
         
        /// <summary>
        /// The default timeout before a request is assumed to have failed.
        /// </summary>
        public static readonly TimeSpan DEFAULT_RESPONSE_TIMEOUT = Debugger.IsAttached ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(30);

        /// <summary>
        /// Minimum period for registering a reminder ... we want to enforce a lower bound
        /// </summary>
        public static readonly TimeSpan MinReminderPeriod = TimeSpan.FromMinutes(1); // increase this period, reminders are supposed to be less frequent ... we use 2 seconds just to reduce the running time of the unit tests
        /// <summary>
        /// Refresh local reminder list to reflect the global reminder table every 'REFRESH_REMINDER_LIST' period
        /// </summary>
        public static readonly TimeSpan RefreshReminderList = TimeSpan.FromMinutes(5);

        public const int LARGE_OBJECT_HEAP_THRESHOLD = 85000;

        public const bool DEFAULT_PROPAGATE_E2E_ACTIVITY_ID = false;

        public const int DEFAULT_LOGGER_BULK_MESSAGE_LIMIT = 5;

        public static readonly bool USE_BLOCKING_COLLECTION = true;

        private static readonly Dictionary<GrainId, string> singletonSystemTargetNames = new Dictionary<GrainId, string>
        {
            {DirectoryServiceId, "DirectoryService"},
            {DirectoryCacheValidatorId, "DirectoryCacheValidator"},
            {SiloControlId,"SiloControl"},
            {ClientObserverRegistrarId,"ClientObserverRegistrar"},
            {CatalogId,"Catalog"},
            {MembershipOracleId,"MembershipOracle"},
            {ReminderServiceId,"ReminderService"},
            {TypeManagerId,"TypeManagerId"},
            {ProviderManagerSystemTargetId, "ProviderManagerSystemTarget"},
            {DeploymentLoadPublisherSystemTargetId, "DeploymentLoadPublisherSystemTarge"},
        };

        private static readonly Dictionary<int, string> nonSingletonSystemTargetNames = new Dictionary<int, string>
        {
            {PULLING_AGENT_SYSTEM_TARGET_TYPE_CODE, "PullingAgentSystemTarget"},
            {PULLING_AGENTS_MANAGER_SYSTEM_TARGET_TYPE_CODE, "PullingAgentsManagerSystemTarget"},
        };

        public static string SystemTargetName(GrainId id)
        {
            string name;
            if (singletonSystemTargetNames.TryGetValue(id, out name)) return name;
            if (nonSingletonSystemTargetNames.TryGetValue(id.GetTypeCode(), out name)) return name;
            return String.Empty;
        }

        public static bool IsSingletonSystemTarget(GrainId id)
        {
            return singletonSystemTargetNames.ContainsKey(id);
        }

        private static readonly Dictionary<GrainId, string> systemGrainNames = new Dictionary<GrainId, string>
        {
            {SystemMembershipTableId, "MembershipTableGrain"},
            {SiloDirectConnectionId, "SiloDirectConnectionId"}
        };

        public static bool TryGetSystemGrainName(GrainId id, out string name)
        {
            return systemGrainNames.TryGetValue(id, out name);
        }

        public static bool IsSystemGrain(GrainId grain)
        {
            return systemGrainNames.ContainsKey(grain);
        }
    }
}
 