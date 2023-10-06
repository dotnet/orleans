namespace Orleans.Clustering.EntityFrameworkCore;

public interface IEFClusterETagConverter<TETag>
{
    TETag ToDbETag(string etag);

    string FromDbETag(TETag etag);
}