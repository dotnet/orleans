using System;
using System.Collections.Generic;
using System.Globalization;
using Orleans.Metadata;

namespace Orleans.Runtime
{
    /// <summary>
    /// The stateless worker placement strategy allows multiple instances of a given grain to co-exist simultaneously on any host and is reserved for stateless worker grains.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    internal class StatelessWorkerPlacement : PlacementStrategy
    {
        private const string MaxLocalPropertyKey = "max-local-instances";
        private static readonly int DefaultMaxStatelessWorkers = Environment.ProcessorCount;

        /// <inheritdoc/>
        public override bool IsUsingGrainDirectory => false;

        /// <summary>
        /// Gets the maximum number of local instances which can be simultaneously active for a given grain.
        /// </summary>
        [Id(1)]
        public int MaxLocal { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="StatelessWorkerPlacement"/> class.
        /// </summary>
        /// <param name="maxLocal">
        /// The maximum number of local instances which can be simultaneously active for a given grain.
        /// </param>
        internal StatelessWorkerPlacement(int maxLocal)
        {
            // If maxLocal was not specified on the StatelessWorkerAttribute,
            // we will use the defaultMaxStatelessWorkers, which is System.Environment.ProcessorCount.
            this.MaxLocal = maxLocal > 0 ? maxLocal : DefaultMaxStatelessWorkers;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="StatelessWorkerPlacement"/> class.
        /// </summary>
        public StatelessWorkerPlacement() : this(-1)
        {
        }

        /// <inheritdoc/>
        public override string ToString() => $"StatelessWorkerPlacement(max={MaxLocal})";

        /// <inheritdoc/>
        public override void Initialize(GrainProperties properties)
        {
            base.Initialize(properties);
            if (properties.Properties.TryGetValue(MaxLocalPropertyKey, out var value)
                && !string.IsNullOrWhiteSpace(value))
            {
                if (int.TryParse(value, out var maxLocal))
                {
                    this.MaxLocal = maxLocal;
                }
            }
        }

        /// <inheritdoc/>
        public override void PopulateGrainProperties(IServiceProvider services, Type grainClass, GrainType grainType, Dictionary<string, string> properties)
        {
            properties[MaxLocalPropertyKey] = this.MaxLocal.ToString(CultureInfo.InvariantCulture);

            base.PopulateGrainProperties(services, grainClass, grainType, properties);
        }
    }
}
