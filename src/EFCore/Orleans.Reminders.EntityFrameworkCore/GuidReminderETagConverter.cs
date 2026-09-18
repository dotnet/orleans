using System;

namespace Orleans.Reminders.EntityFrameworkCore;

public sealed class GuidReminderETagConverter : IEFReminderETagConverter<Guid>
{
    public Guid ToDbETag(string etag) => Guid.Parse(etag);

    public string FromDbETag(Guid etag) => etag.ToString("D");
}
