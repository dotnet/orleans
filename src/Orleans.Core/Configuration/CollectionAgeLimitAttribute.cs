using System;
using System.Collections.Generic;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// Specifies the period of inactivity before a grain is available for collection and deactivation.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class CollectionAgeLimitAttribute : Attribute, IGrainPropertiesProviderAttribute
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

        /// <inheritdoc />
        public void Populate(IServiceProvider services, Type grainClass, GrainType grainType, Dictionary<string, string> properties)
        {
            properties[WellKnownGrainTypeProperties.IdleDeactivationPeriod] = this.Amount.ToString("c");
        }
    }

    /// <summary>
    /// Specifies the period of inactivity before a grain is available for collection and deactivation.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class KeepAliveAttribute : Attribute, IGrainPropertiesProviderAttribute
    {
        public void Populate(IServiceProvider services, Type grainClass, GrainType grainType, Dictionary<string, string> properties)
        {
            properties[WellKnownGrainTypeProperties.IdleDeactivationPeriod] = WellKnownGrainTypeProperties.IndefiniteIdleDeactivationPeriodValue;
        }
    }
}
