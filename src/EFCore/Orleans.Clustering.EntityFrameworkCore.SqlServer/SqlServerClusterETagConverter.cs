using System;

namespace Orleans.Clustering.EntityFrameworkCore.SqlServer;

internal class SqlServerClusterETagConverter : IEFClusterETagConverter<byte[]>
{
    public byte[] ToDbETag(string etag) => BitConverter.GetBytes(ulong.Parse(etag));

    public string FromDbETag(byte[] etag) => BitConverter.ToUInt64(etag).ToString();
}