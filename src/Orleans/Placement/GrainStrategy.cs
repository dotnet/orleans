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

namespace Orleans.Runtime
{
    /// <summary>
    /// Strategy that applies to an individual grain
    /// </summary>
    [Serializable]
    internal abstract class GrainStrategy
    {
        /// <summary>
        /// Placement strategy that indicates that new activations of this grain type should be placed randomly,
        /// subject to the overall placement policy.
        /// </summary>
        public static PlacementStrategy RandomPlacement;
        /// <summary>
        /// Placement strategy that indicates that new activations of this grain type should be placed on a local silo.
        /// </summary>
        public static PlacementStrategy PreferLocalPlacement;

        /// <summary>
        /// Placement strategy that indicates that new activations of this grain type should be placed
        /// subject to the current load distribution across the deployment.
        /// This Placement that takes into account CPU/Memory/ActivationCount.
        /// </summary>
        public static PlacementStrategy ActivationCountBasedPlacement;
        /// <summary>
        /// Use a graph partitioning algorithm
        /// </summary>
        internal static PlacementStrategy GraphPartitionPlacement;

        internal static void InitDefaultGrainStrategies()
        {
            RandomPlacement = Orleans.Runtime.RandomPlacement.Singleton;

            PreferLocalPlacement = Orleans.Runtime.PreferLocalPlacement.Singleton;

            ActivationCountBasedPlacement = Orleans.Runtime.ActivationCountBasedPlacement.Singleton;
        }
    }
}
