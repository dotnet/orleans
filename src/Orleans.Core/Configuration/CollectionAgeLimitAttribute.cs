using System;

namespace Orleans.Configuration
{
    /// <summary>
    /// Specifies the period of inactivity before a grain is available for collection and deactivation.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class CollectionAgeLimitAttribute : Attribute
    {
        public const int DefaultAgeMinutes = 10;

        public int Days { get; set; } = 0;
        public int Hours { get; set; } = 0;
        public int Minutes { get; set; } = 0;

        public bool AlwaysActive { get; set; }

        public TimeSpan Amount
        {
            get
            {
                var span = AlwaysActive
                ? TimeSpan.FromDays(short.MaxValue)
                : TimeSpan.FromDays(Days) + TimeSpan.FromHours(Hours) + TimeSpan.FromMinutes(Minutes);
                return span < TimeSpan.FromMinutes(DefaultAgeMinutes)
                    ? TimeSpan.FromMinutes(DefaultAgeMinutes)
                    : span;
            }
        }
    }
}
