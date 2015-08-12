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

﻿namespace Orleans.Runtime
{
    /// <summary>
    /// Possible statuses of a silo.
    /// </summary>
    public enum SiloStatus
    {
        None = 0,
        /// <summary>
        /// This silo was just created, but not started yet.
        /// </summary>
        Created = 1,
        /// <summary>
        /// This silo has just started, but not ready yet. It is attempting to join the cluster.
        /// </summary>
        Joining = 2,         
        /// <summary>
        /// This silo is alive and functional.
        /// </summary>
        Active = 3,
        /// <summary>
        /// This silo is shutting itself down.
        /// </summary>
        ShuttingDown = 4,    
        /// <summary>
        /// This silo is stopping itself down.
        /// </summary>
        Stopping = 5,
        /// <summary>
        /// This silo is de-activated/considered to be dead.
        /// </summary>
        Dead = 6
    }

    public static class SiloStatusExtensions
    {
        /// <summary>
        /// Return true if this silo is currently terminating: ShuttingDown, Stopping or Dead.
        /// </summary>
        public static bool IsTerminating(this SiloStatus siloStatus)
        {
            return siloStatus.Equals(SiloStatus.ShuttingDown) || siloStatus.Equals(SiloStatus.Stopping) || siloStatus.Equals(SiloStatus.Dead);
        }
    }
}