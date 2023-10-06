namespace Orleans.Reminders.EntityFrameworkCore;

public interface IEFReminderETagConverter<TETag>
{
    TETag ToDbETag(string etag);

    string FromDbETag(TETag etag);
}