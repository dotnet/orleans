using System;

namespace Orleans.GrainDirectory.EntityFrameworkCore;

public sealed class GuidGrainDirectoryETagConverter : IEFGrainDirectoryETagConverter<Guid>
{
    public Guid ToDbETag(string etag) => Guid.Parse(etag);

    public string FromDbETag(Guid etag) => etag.ToString("D");
}
