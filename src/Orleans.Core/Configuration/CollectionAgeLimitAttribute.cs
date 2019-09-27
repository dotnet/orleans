using System;

namespace Orleans.Configuration
{
    /// <summary>
    /// Specifies the period of inactivity before a grain is available for collection and deactivation.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class CollectionAgeLimitAttribute : Attribute
    {
        public static readonly TimeSpan DEFAULT_COLLECTION_AGE_LIMIT = TimeSpan.FromHours(2);

        public readonly TimeSpan MinAgeLimit = TimeSpan.FromMinutes(1);

        public double Days { get; set; } 
        public double Hours { get; set; } 
        public double Minutes { get; set; } 

        public bool AlwaysActive { get; set; }

        public TimeSpan Amount
        {
            get
            {
                var span = AlwaysActive
                ? TimeSpan.FromDays(short.MaxValue)
                : TimeSpan.FromDays(Days) + TimeSpan.FromHours(Hours) + TimeSpan.FromMinutes(Minutes);
                return span <= TimeSpan.Zero
                    ? MinAgeLimit
                    : span;
            }
        }
    }
}
