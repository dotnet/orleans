using System;
using System.Globalization;
using Orleans.Clustering.EntityFrameworkCore;

namespace Orleans.Clustering;

public class MySqlClusterETagConverter : IEFClusterETagConverter<DateTime>
{
    public DateTime ToDbETag(string etag) => DateTime.Parse(etag, CultureInfo.InvariantCulture);

    public string FromDbETag(DateTime etag) => etag.ToString(CultureInfo.InvariantCulture);
}