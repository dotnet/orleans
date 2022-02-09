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
        /// <summary>
        /// Gets the minimum activation age.
        /// </summary>
        public readonly TimeSpan MinAgeLimit = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Gets or sets the number of days to delay collecting an idle activation for.
        /// </summary>
        public double Days { get; set; } 

        /// <summary>
        /// Gets or sets the number of hours to delay collecting an idle activation for.
        /// </summary>
        public double Hours { get; set; } 

        /// <summary>
        /// Gets or sets the number of minutes to delay collecting an idle activation for.
        /// </summary>
        public double Minutes { get; set; } 

        /// <summary>
        /// Gets or sets a value indicating whether this grain should never be collected by the idle activation collector.
        /// </summary>
        public bool AlwaysActive { get; set; }

        /// <summary>
        /// Gets the idle activation collection age.
        /// </summary>
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
            string idleDeactivationPeriod;

            if (AlwaysActive)
            {
                idleDeactivationPeriod = WellKnownGrainTypeProperties.IndefiniteIdleDeactivationPeriodValue;
            }
            else
            {
                idleDeactivationPeriod = this.Amount.ToString("c");
            }

            properties[WellKnownGrainTypeProperties.IdleDeactivationPeriod] = idleDeactivationPeriod;
        }
    }

    /// <summary>
    /// When applied to a grain implementation type this attribute specifies that activations of the grain should should not be collected by the idle activation collector.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class KeepAliveAttribute : Attribute, IGrainPropertiesProviderAttribute
    {
        /// <inheritdoc />
        public void Populate(IServiceProvider services, Type grainClass, GrainType grainType, Dictionary<string, string> properties)
        {
            properties[WellKnownGrainTypeProperties.IdleDeactivationPeriod] = WellKnownGrainTypeProperties.IndefiniteIdleDeactivationPeriodValue;
        }
    }
}
