using System;

namespace Orleans.Configuration
{
    internal class CollectionAgeLimitConstants
    {
        public static readonly TimeSpan DefaultCollectionAgeLimit = TimeSpan.FromHours(2);
    }
}
