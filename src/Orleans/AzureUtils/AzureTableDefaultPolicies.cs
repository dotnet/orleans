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

﻿using System;
using System.Diagnostics;
using Microsoft.WindowsAzure.Storage.RetryPolicies;
using Orleans.Runtime;


namespace Orleans.AzureUtils
{
    /// <summary>
    /// Utility class for default retry / timeout settings for Azure storage.
    /// </summary>
    /// <remarks>
    /// These functions are mostly intended for internal usage by Orleans runtime, but due to certain assembly packaging constrants this class needs to have public visibility.
    /// </remarks>
    internal static class AzureTableDefaultPolicies
    {
        public static int MaxTableCreationRetries { get; private set; }
        public static int MaxTableOperationRetries { get; private set; }
        public static int MaxBusyRetries { get; internal set; }

        public static TimeSpan PauseBetweenTableCreationRetries { get; private set; }
        public static TimeSpan PauseBetweenTableOperationRetries { get; private set; }
        public static TimeSpan PauseBetweenBusyRetries { get; private set; }

        public static TimeSpan TableCreationTimeout { get; private set; }
        public static TimeSpan TableOperationTimeout { get; private set; }
        public static TimeSpan BusyRetriesTimeout { get; private set; }

        public static IRetryPolicy TableCreationRetryPolicy { get; private set; }
        public static IRetryPolicy TableOperationRetryPolicy { get; private set; }

        public const int MAX_BULK_UPDATE_ROWS = 100;

        static AzureTableDefaultPolicies()
        {
            MaxTableCreationRetries = 60;
            PauseBetweenTableCreationRetries = TimeSpan.FromSeconds(1);

            MaxTableOperationRetries = 5;
            PauseBetweenTableOperationRetries = TimeSpan.FromMilliseconds(100);

            MaxBusyRetries = 120;
            PauseBetweenBusyRetries = TimeSpan.FromMilliseconds(500);
#if DEBUG
            if (Debugger.IsAttached)
            {
                PauseBetweenTableCreationRetries = PauseBetweenTableCreationRetries.Multiply(100);
                PauseBetweenTableOperationRetries = PauseBetweenTableOperationRetries.Multiply(100);
                PauseBetweenBusyRetries = PauseBetweenBusyRetries.Multiply(10);
            }
#endif
            TableCreationRetryPolicy = new LinearRetry(PauseBetweenTableCreationRetries, MaxTableCreationRetries); // 60 x 1s
            TableCreationTimeout = PauseBetweenTableCreationRetries.Multiply(MaxTableCreationRetries).Multiply(3);    // 3 min

            TableOperationRetryPolicy = new LinearRetry(PauseBetweenTableOperationRetries, MaxTableOperationRetries); // 5 x 100ms
            TableOperationTimeout = PauseBetweenTableOperationRetries.Multiply(MaxTableOperationRetries).Multiply(6);    // 3 sec

            BusyRetriesTimeout = PauseBetweenBusyRetries.Multiply(MaxBusyRetries);  // 1 minute
        }
    }
}
