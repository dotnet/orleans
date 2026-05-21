using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime;

internal interface IGrainContextMigration
{
    bool TryStartMigration(
        Dictionary<string, object>? requestContext,
        [NotNullWhen(true)] out Task? deactivated,
        CancellationToken cancellationToken = default);
}
