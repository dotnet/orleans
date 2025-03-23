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
        private const string RemoveIdleWorkersPropertyKey = "remove-idle-workers";

        private static readonly int DefaultMaxStatelessWorkers = Environment.ProcessorCount;

        /// <inheritdoc/>
        public override bool IsUsingGrainDirectory => false;

        /// <summary>
        /// Gets the maximum number of local instances which can be simultaneously active for a given grain.
        /// </summary>
        [Id(0)]
        public int MaxLocal { get; private set; }

        /// <summary>
        /// When set to <see langword="true"/>, idle workers will be proactively deactivated by the runtime.
        /// Otherwise if <see langword="false"/>, than the workers will be deactivated according to collection age.
        /// </summary>
        [Id(1)]
        public bool RemoveIdleWorkers { get; private set; } = true;

        /// <summary>
        /// Initializes a new instance of the <see cref="StatelessWorkerPlacement"/> class.
        /// </summary>
        /// <param name="maxLocal">
        /// The maximum number of local instances which can be simultaneously active for a given grain.
        /// </param>
        internal StatelessWorkerPlacement(int maxLocal) : this(maxLocal, true)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="StatelessWorkerPlacement"/> class.
        /// </summary>
        /// <param name="maxLocal">
        /// The maximum number of local instances which can be simultaneously active for a given grain.
        /// </param>
        /// <param name="removeIdleWorkers">
        /// Whether idle workers will be proactively deactivated by the runtime instead of only being deactivated according to collection age.
        /// </param>
        internal StatelessWorkerPlacement(int maxLocal, bool removeIdleWorkers)
        {
            // If maxLocal was not specified on the StatelessWorkerAttribute,
            // we will use the defaultMaxStatelessWorkers, which is System.Environment.ProcessorCount.
            this.MaxLocal = maxLocal > 0 ? maxLocal : DefaultMaxStatelessWorkers;
            this.RemoveIdleWorkers = removeIdleWorkers;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="StatelessWorkerPlacement"/> class.
        /// </summary>
        public StatelessWorkerPlacement() : this(-1)
        {
        }

        /// <inheritdoc/>
        public override string ToString() => $"StatelessWorkerPlacement(MaxLocal={MaxLocal}, RemoveIdleWorkers={RemoveIdleWorkers})";

        /// <inheritdoc/>
        public override void Initialize(GrainProperties properties)
        {
            base.Initialize(properties);

            if (properties.Properties.TryGetValue(MaxLocalPropertyKey, out var maxLocalValue) &&
                !string.IsNullOrWhiteSpace(maxLocalValue))
            {
                if (int.TryParse(maxLocalValue, out var maxLocal))
                {
                    MaxLocal = maxLocal;
                }
            }

            if (properties.Properties.TryGetValue(RemoveIdleWorkersPropertyKey, out var removeIdleValue) &&
                !string.IsNullOrWhiteSpace(removeIdleValue))
            {
                if (bool.TryParse(removeIdleValue, out var removeIdle))
                {
                    RemoveIdleWorkers = removeIdle;
                }
            }
        }

        /// <inheritdoc/>
        public override void PopulateGrainProperties(IServiceProvider services, Type grainClass, GrainType grainType, Dictionary<string, string> properties)
        {
            properties[MaxLocalPropertyKey] = MaxLocal.ToString(CultureInfo.InvariantCulture);
            properties[RemoveIdleWorkersPropertyKey] = RemoveIdleWorkers.ToString(CultureInfo.InvariantCulture);

            base.PopulateGrainProperties(services, grainClass, grainType, properties);
        }
    }
}