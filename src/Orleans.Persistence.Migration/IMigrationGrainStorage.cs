using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Persistence.Migration
{
    public interface IMigrationGrainStorage : IGrainStorage
    {
        /// <summary>
        /// Similar to <see cref="IGrainStorage.WriteStateAsync(string, GrainReference, IGrainState)"/> but for migrating grain state from one storage to another.
        /// Can do some extra work to ensure a proper migration or data preparation.
        /// </summary>
        Task<GrainReference> MigrateGrainStateAsync(string grainType, GrainReference grainReference, IGrainState grainState);
    }
}
