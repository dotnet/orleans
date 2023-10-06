using System;
using Orleans.GrainDirectory.EntityFrameworkCore;

namespace Orleans.GrainDirectory;

internal class SqlServerGrainDirectoryETagConverter : IEFGrainDirectoryETagConverter<byte[]>
{
    public byte[] ToDbETag(string etag) => BitConverter.GetBytes(ulong.Parse(etag));

    public string FromDbETag(byte[] etag) => BitConverter.ToUInt64(etag).ToString();
}