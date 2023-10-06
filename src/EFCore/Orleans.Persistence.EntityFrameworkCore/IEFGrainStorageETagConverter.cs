namespace Orleans.Persistence.EntityFrameworkCore;

public interface IEFGrainStorageETagConverter<TETag>
{
    TETag ToDbETag(string etag);

    string FromDbETag(TETag etag);
}