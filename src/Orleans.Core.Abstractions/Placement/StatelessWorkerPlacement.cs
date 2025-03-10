using System;
using System.Collections.Generic;
using System.Globalization;
using Orleans.Metadata;

namespace Orleans.Runtime
{
    /// <summary>
    /// The stateless worker placement strategy allows multiple instances of a given grain to co-exist simultaneously on any host and is reserved for stateless worker grains.
    /// </summary>
    [Serializable, GenerateSerializer]
    internal sealed class StatelessWorkerPlacement : PlacementStrategy
    {
        private const string MaxLocalPropertyKey = "max-local-instances";
        private const string ProactiveCollectionPropertyKey = "proactive-removal";

        private static readonly int DefaultMaxStatelessWorkers = Environment.ProcessorCount;

        /// <inheritdoc/>
        public override bool IsUsingGrainDirectory => false;

        /// <summary>
        /// Gets the maximum number of local instances which can be simultaneously active for a given grain.
        /// </summary>
        [Id(0)]
        public int MaxLocal { get; private set; }

        /// <summary>
        /// Instructs the runtime whether it should eagerly collect idle workers based on concurrent load or let them be collected by the activation collector.
        /// </summary>
        [Id(1)]
        public bool ProactiveWorkerCollection { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="StatelessWorkerPlacement"/> class.
        /// </summary>
        /// <param name="maxLocal">
        /// The maximum number of local instances which can be simultaneously active for a given grain.
        /// </param>
        /// <param name="proactiveWorkerCollection">
        /// Wether to proactively collect workers if they are inactive, or let them be collected by the activation collector.
        /// </param>
        internal StatelessWorkerPlacement(int maxLocal, bool proactiveWorkerCollection)
        {
            // If maxLocal was not specified on the StatelessWorkerAttribute,
            // we will use the defaultMaxStatelessWorkers, which is System.Environment.ProcessorCount.
            this.MaxLocal = maxLocal > 0 ? maxLocal : DefaultMaxStatelessWorkers;
            this.ProactiveWorkerCollection = proactiveWorkerCollection;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="StatelessWorkerPlacement"/> class.
        /// </summary>
        public StatelessWorkerPlacement() : this(-1, false)
        {
        }

        /// <inheritdoc/>
        public override string ToString() => $"StatelessWorkerPlacement(max={MaxLocal} | (proactive={ProactiveWorkerCollection}))";

        /// <inheritdoc/>
        public override void Initialize(GrainProperties properties)
        {
            base.Initialize(properties);

            if (properties.Properties.TryGetValue(MaxLocalPropertyKey, out var value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                if (int.TryParse(value, out var maxLocal))
                {
                    MaxLocal = maxLocal;
                }
            }

            if (properties.Properties.TryGetValue(ProactiveCollectionPropertyKey, out var proactive) &&
                !string.IsNullOrWhiteSpace(proactive))
            {
                if (bool.TryParse(proactive, out var isProactive))
                {
                    ProactiveWorkerCollection = isProactive;
                }
            }
        }

        /// <inheritdoc/>
        public override void PopulateGrainProperties(IServiceProvider services, Type grainClass, GrainType grainType, Dictionary<string, string> properties)
        {
            properties[MaxLocalPropertyKey] = MaxLocal.ToString(CultureInfo.InvariantCulture);
            properties[ProactiveCollectionPropertyKey] = ProactiveWorkerCollection.ToString();

            base.PopulateGrainProperties(services, grainClass, grainType, properties);
        }
    }
}