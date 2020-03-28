using System.Runtime.CompilerServices;

namespace Orleans.Runtime
{
    /// <summary>
    /// This class holds information regarding the mapping of grains
    /// to their activation data.
    /// </summary>
    /// <remarks>
    /// Classic grains directly hold a reference to their activation data. However
    /// to support grains that do not inherit from `Grain`, we need a way to statically
    /// map from the grain to the ActivationData. Enter ConditionalWeakTable, which
    /// is deigned to associate an extra value with an object instance as if it were
    /// a property on said object. It has special garbage collector support.
    /// </remarks>
    internal static class ActivationDataLookup
    {
        internal static ConditionalWeakTable<IGrain, IActivationData> mappingTable = new ConditionalWeakTable<IGrain, IActivationData>();

        internal static IActivationData GetActivationData(this IGrain grain)
        {
            if (grain is Grain classicGrain)
            {
                return classicGrain.Data;
            }
            
            return mappingTable.TryGetValue(grain, out var activationData) ? activationData : null;
        }

        internal static void AssociateActivationData(IGrain grain, IActivationData activationData)
        {
            if (grain is Grain classicGrain)
            {
                classicGrain.Data = activationData;
            }

            mappingTable.Add(grain, activationData);
        }
    }
}
