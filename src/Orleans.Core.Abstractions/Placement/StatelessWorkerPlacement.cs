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
        private const string OperatingModePropertyKey = "sw-operating-mode";

        private static readonly int DefaultMaxStatelessWorkers = Environment.ProcessorCount;

        /// <inheritdoc/>
        public override bool IsUsingGrainDirectory => false;

        /// <summary>
        /// Gets the maximum number of local instances which can be simultaneously active for a given grain.
        /// </summary>
        [Id(0)]
        public int MaxLocal { get; private set; }

        [Id(1)]
        public StatelessWorkerOperatingMode OperatingMode { get; private set; } 

        /// <summary>
        /// Initializes a new instance of the <see cref="StatelessWorkerPlacement"/> class.
        /// </summary>
        /// <param name="maxLocal">
        /// The maximum number of local instances which can be simultaneously active for a given grain.
        /// </param>
        internal StatelessWorkerPlacement(int maxLocal, StatelessWorkerOperatingMode mode)
        {
            // If maxLocal was not specified on the StatelessWorkerAttribute,
            // we will use the defaultMaxStatelessWorkers, which is System.Environment.ProcessorCount.
            this.MaxLocal = maxLocal > 0 ? maxLocal : DefaultMaxStatelessWorkers;
            this.OperatingMode = mode;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="StatelessWorkerPlacement"/> class.
        /// </summary>
        public StatelessWorkerPlacement() : this(-1, StatelessWorkerOperatingMode.Monotonic)
        {
        }

        /// <inheritdoc/>
        public override string ToString() => $"StatelessWorkerPlacement(max={MaxLocal} | (mode={OperatingMode}))";

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

            if (properties.Properties.TryGetValue(OperatingModePropertyKey, out var modeValue) &&
                !string.IsNullOrWhiteSpace(modeValue))
            {
                if (byte.TryParse(modeValue, out var mode))
                {
                    OperatingMode = (StatelessWorkerOperatingMode)mode;
                }
            }
        }

        /// <inheritdoc/>
        public override void PopulateGrainProperties(IServiceProvider services, Type grainClass, GrainType grainType, Dictionary<string, string> properties)
        {
            properties[MaxLocalPropertyKey] = MaxLocal.ToString(CultureInfo.InvariantCulture);
            properties[OperatingModePropertyKey] = ((byte)OperatingMode).ToString();

            base.PopulateGrainProperties(services, grainClass, grainType, properties);
        }
    }

    public enum StatelessWorkerOperatingMode : byte
    {
        Monotonic = 0,
        Adaptive = 1
    }
}
