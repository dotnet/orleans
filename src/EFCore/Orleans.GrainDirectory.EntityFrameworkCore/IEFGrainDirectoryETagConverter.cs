namespace Orleans.GrainDirectory.EntityFrameworkCore;

public interface IEFGrainDirectoryETagConverter<TETag>
{
    TETag ToDbETag(string etag);

    string FromDbETag(TETag etag);
}