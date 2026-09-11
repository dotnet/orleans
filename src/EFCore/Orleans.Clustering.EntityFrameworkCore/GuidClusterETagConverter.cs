using System;

namespace Orleans.Clustering.EntityFrameworkCore;

public sealed class GuidClusterETagConverter : IEFClusterETagConverter<Guid>
{
    public Guid ToDbETag(string etag) => Guid.Parse(etag);

    public string FromDbETag(Guid etag) => etag.ToString("D");
}
