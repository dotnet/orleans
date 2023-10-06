using System;
using Orleans.Persistence.EntityFrameworkCore;

namespace Orleans.Persistence;

internal class SqlServerGrainStateETagConverter : IEFGrainStorageETagConverter<byte[]>
{
    public byte[] ToDbETag(string etag) => BitConverter.GetBytes(ulong.Parse(etag));

    public string FromDbETag(byte[] etag) => BitConverter.ToUInt64(etag).ToString();
}