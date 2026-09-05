using System;
using System.Globalization;
using Orleans.Clustering.EntityFrameworkCore;

namespace Orleans.Clustering.EntityFrameworkCore.SqlServer;

internal class SqlServerClusterETagConverter : IEFClusterETagConverter<byte[]>
{
    public byte[] ToDbETag(string etag) =>
        BitConverter.GetBytes(ulong.Parse(etag, NumberStyles.None, CultureInfo.InvariantCulture));

    public string FromDbETag(byte[] etag)
    {
        ArgumentNullException.ThrowIfNull(etag);
        if (etag.Length != sizeof(ulong))
        {
            throw new ArgumentOutOfRangeException(
                nameof(etag),
                etag.Length,
                $"SQL Server rowversion ETags must contain exactly {sizeof(ulong)} bytes.");
        }

        return BitConverter.ToUInt64(etag).ToString(CultureInfo.InvariantCulture);
    }
}
