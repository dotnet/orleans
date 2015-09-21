﻿/*
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

namespace Orleans.Streams
{
    public enum StreamQueueBalancerType
    {
        /// <summary>
        /// Stream queue balancer that uses consistent ring provider for load balancing
        /// </summary>
        ConsistentRingBalancer, 

        /// <summary>
        /// Stream queue balancer that uses Azure deployment information and silo statuses from Membership oracle for load balancing.  
        /// Requires silo running in Azure.
        /// This Balancer uses both the information about the full set of silos as reported by Azure role code and 
        /// the information from Membership oracle about currently active (alive) silos and rebalances queues from non active silos.
        /// </summary>
        DynamicAzureDeploymentBalancer,

        /// <summary>
        /// Stream queue balancer that uses Azure deployment information for load balancing. 
        /// Requires silo running in Azure.
        /// This Balancer uses both the information about the full set of silos as reported by Azure role code but 
        /// does NOT use the information from Membership oracle about currently alive silos. 
        /// That is, it does not rebalance queues based on dymanic changes in the cluster Membership.
        /// </summary>
        StaticAzureDeploymentBalancer, 

        /// <summary>
        /// Stream queue balancer that uses the cluster configuration to determine deployment information for load balancing.  
        /// It does not support dynamic changes to global (full) cluster configuration.
        /// This Balancer does use the information from Membership oracle about currently active (alive) silos 
        /// and rebalances queues from non active silos.
        /// </summary>
        DynamicClusterConfigDeploymentBalancer,

        /// <summary>
        /// Stream queue balancer that uses the cluster configuration to determine deployment information for load balancing.  
        /// It does not support dynamic changes to global (full) cluster configuration.
        /// This Balancer does NOT use the information from Membership oracle about currently active silos.
        /// That is, it does not rebalance queues based on dymanic changes in the cluster Membership.
        /// </summary>
        StaticClusterConfigDeploymentBalancer,
    }
}
