using System.Collections.Generic;
using System.Threading;

namespace Orleans.Runtime;

internal interface IGrainContextMigration
{
    bool TryStartMigration(Dictionary<string, object>? requestContext, CancellationToken cancellationToken = default);
}
