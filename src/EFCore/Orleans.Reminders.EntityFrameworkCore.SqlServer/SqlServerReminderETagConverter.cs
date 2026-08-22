using System;
using Orleans.Reminders.EntityFrameworkCore;

namespace Orleans.Reminders;

public class SqlServerReminderETagConverter : IEFReminderETagConverter<byte[]>
{
    public byte[] ToDbETag(string etag) => BitConverter.GetBytes(ulong.Parse(etag));

    public string FromDbETag(byte[] etag) => BitConverter.ToUInt64(etag).ToString();
}