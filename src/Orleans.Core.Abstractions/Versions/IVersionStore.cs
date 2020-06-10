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
        Task<Dictionary<GrainInterfaceId, CompatibilityStrategy>> GetCompatibilityStrategies();
        Task<Dictionary<GrainInterfaceId, VersionSelectorStrategy>> GetSelectorStrategies();
        Task<CompatibilityStrategy> GetCompatibilityStrategy();
        Task<VersionSelectorStrategy> GetSelectorStrategy();
    }
}