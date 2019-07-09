using System;
using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.Placement
{

    /// <summary>
    /// Base for all placement policy marker attributes.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public abstract class PlacementAttribute : Attribute
    {
        public PlacementStrategy PlacementStrategy { get; private set; }

        protected PlacementAttribute(PlacementStrategy placement)
        {
            if (placement == null) throw new ArgumentNullException(nameof(placement));

            this.PlacementStrategy = placement;
        }
    }

    /// <summary>
    /// Marks a grain class as using the <c>RandomPlacement</c> policy.
    /// </summary>
    /// <remarks>
    /// This is the default placement policy, so this attribute does not need to be used for normal grains.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class RandomPlacementAttribute : PlacementAttribute
    {
        public RandomPlacementAttribute() :
            base(RandomPlacement.Singleton)
        {
        }
    }

    /// <summary>
    /// Marks a grain class as using the <c>HashBasedPlacement</c> policy.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class HashBasedPlacementAttribute : PlacementAttribute
    {
        public HashBasedPlacementAttribute() :
            base(HashBasedPlacement.Singleton)
        { }
    }

    /// <summary>
    /// Marks a grain class as using the <c>PreferLocalPlacement</c> policy.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class PreferLocalPlacementAttribute : PlacementAttribute
    {
        public PreferLocalPlacementAttribute() :
            base(PreferLocalPlacement.Singleton)
        {
        }
    }

    /// <summary>
    /// Marks a grain class as using the <c>ActivationCountBasedPlacement</c> policy.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class ActivationCountBasedPlacementAttribute : PlacementAttribute
    {
        public ActivationCountBasedPlacementAttribute() :
            base(ActivationCountBasedPlacement.Singleton)
        {
        }
    }

    /// <summary>
    /// Marks a grain class as using the <c>SiloPlacement</c> policy.
    /// </summary>
    /// <remarks>
    /// This indicates the grain should always be placed on the target silo.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class SiloServicePlacementAttribute : PlacementAttribute
    {
        public SiloServicePlacementAttribute() :
            base(SiloServicePlacement.Singleton)
        {
        }
    }

    public static class SiloServicePlacementKeyFormat
    {
        private const char Separator = '|';
        private static readonly char[] Separators = { Separator };

        public static bool TryParsePrimaryKey(string primaryKey, out string key)
        {
            key = default(string);
            int index = primaryKey.IndexOfAny(Separators);
            if (index == -1)
                return false;
            if (index == primaryKey.Length - 1)
            {
                key = string.Empty;
                return true;
            }
            key = primaryKey.Substring(index + 1);
            return true;
        }

        internal static bool TryParsePrimaryKey(IEnumerable<SiloAddress> silos, string primaryKey, out (SiloAddress Silo, string Key) parsedKey)
        {
            parsedKey = default;
            foreach (SiloAddress silo in silos)
            {
                string siloTag = BuildSiloTag(silo);
                if (primaryKey.StartsWith(siloTag))
                {
                    parsedKey = (silo, primaryKey.Substring(siloTag.Length));
                    return true;
                }
            }
            return false;
        }

        internal static string BuildPrimaryKey(SiloAddress silo, string key)
        {
            return $"{BuildSiloTag(silo)}{key}";
        }


        private static string BuildSiloTag(SiloAddress silo)
        {
            string siloKey = silo.ToParsableString();
            // Validate silo key has no Separator
            if (siloKey.IndexOfAny(Separators) != -1) throw new ArgumentOutOfRangeException(nameof(silo), "Silo parsable string format error");
            return $"{siloKey}{Separator}";
        }
    }

    public static class PlacementGrainFactoryExtensions
    {
        public static TGrainInterface GetGrain<TGrainInterface>(this IGrainFactory grainFactory, SiloAddress silo, string primaryKey = null, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey
        {
            return grainFactory.GetGrain<TGrainInterface>(SiloServicePlacementKeyFormat.BuildPrimaryKey(silo, primaryKey), grainClassNamePrefix);
        }
    }
}
