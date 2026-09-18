using System;

namespace Orleans.Persistence.EntityFrameworkCore;

public sealed class GuidGrainStorageETagConverter : IEFGrainStorageETagConverter<Guid>
{
    public Guid ToDbETag(string etag) => Guid.Parse(etag);

    public string FromDbETag(Guid etag) => etag.ToString("D");
}
