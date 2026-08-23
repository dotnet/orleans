using System;
using System.Globalization;
using Orleans.Persistence.EntityFrameworkCore;

namespace Orleans.Persistence;

internal class SqlServerGrainStateETagConverter : IEFGrainStorageETagConverter<byte[]>
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