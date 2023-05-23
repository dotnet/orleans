using System.Collections.Generic;
using System.Threading.Tasks;


namespace Orleans.Runtime
{
    /// <summary>
    /// Remote interface to grain and activation state
    /// </summary>
    internal interface ICatalog : ISystemTarget
    {
        /// <summary>
        /// Delete activations from this silo
        /// </summary>
        /// <param name="activationAddresses"></param>
        /// <param name="reasonCode"></param>
        /// <param name="reasonText"></param>
        /// <returns></returns>
        Task DeleteActivations(List<GrainAddress> activationAddresses, DeactivationReasonCode reasonCode, string reasonText);

        /// <summary>
        /// Accepts migrating grains.
        /// </summary>
        ValueTask AcceptMigratingGrains(List<GrainMigrationPackage> migratingGrains);
    }

    [GenerateSerializer, Immutable]
    internal struct GrainMigrationPackage
    {
        [Id(0)]
        public GrainId GrainId { get; set; }

        [Id(1)]
        public MigrationContext MigrationContext { get; set; }
    }
}
