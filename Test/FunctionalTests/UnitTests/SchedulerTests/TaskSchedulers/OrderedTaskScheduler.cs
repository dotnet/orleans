//--------------------------------------------------------------------------
// 
//  Copyright (c) Microsoft Corporation.  All rights reserved. 
// 
//  File: OrderedTaskScheduler.cs
//
//--------------------------------------------------------------------------

namespace System.Threading.Tasks.Schedulers
{
    /// <summary>
    /// Provides a task scheduler that ensures only one task is executing at a time, 
    /// and that tasks execute in the order that they were queued.
    /// </summary>
    public sealed class OrderedTaskScheduler : LimitedConcurrencyLevelTaskScheduler
    {
        /// <summary>Initializes an instance of the OrderedTaskScheduler class.</summary>
        public OrderedTaskScheduler() : base(1) { }
    }
}