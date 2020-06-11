using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;

namespace Orleans.Versions
{
    public interface IVersionStore : IVersionManager
    {
        bool IsEnabled { get; }
        Task<Dictionary<GrainInterfaceType, CompatibilityStrategy>> GetCompatibilityStrategies();
        Task<Dictionary<GrainInterfaceType, VersionSelectorStrategy>> GetSelectorStrategies();
        Task<CompatibilityStrategy> GetCompatibilityStrategy();
        Task<VersionSelectorStrategy> GetSelectorStrategy();
    }
}