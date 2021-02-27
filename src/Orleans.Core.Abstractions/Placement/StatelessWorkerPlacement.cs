using System;
using System.Collections.Generic;
using System.Globalization;
using Orleans.Metadata;

namespace Orleans.Runtime
{
    [Serializable]
    [GenerateSerializer]
    internal class StatelessWorkerPlacement : PlacementStrategy
    {
        private const string MaxLocalPropertyKey = "max-local-instances";
        private static readonly int DefaultMaxStatelessWorkers = Environment.ProcessorCount;

        /// <summary>
        /// Stateless workers are not registered in the grain directory.
        /// </summary>
        public override bool IsUsingGrainDirectory => false;

        [Id(1)]
        public int MaxLocal { get; private set; }

        internal StatelessWorkerPlacement(int maxLocal)
        {
            // If maxLocal was not specified on the StatelessWorkerAttribute, 
            // we will use the defaultMaxStatelessWorkers, which is System.Environment.ProcessorCount.
            this.MaxLocal = maxLocal > 0 ? maxLocal : DefaultMaxStatelessWorkers;
        }

        public StatelessWorkerPlacement() : this(-1)
        {
        }

        public override string ToString()
        {
            return string.Format("StatelessWorkerPlacement(max={0})", this.MaxLocal);
        }

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

        public override void PopulateGrainProperties(IServiceProvider services, Type grainClass, GrainType grainType, Dictionary<string, string> properties)
        {
            properties[MaxLocalPropertyKey] = this.MaxLocal.ToString(CultureInfo.InvariantCulture);

            base.PopulateGrainProperties(services, grainClass, grainType, properties);
        }
    }
}
