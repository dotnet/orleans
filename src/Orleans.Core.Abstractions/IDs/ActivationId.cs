using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{
    [Serializable]
    public class ActivationId : IEquatable<ActivationId>
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        [DataMember]
        protected readonly internal UniqueKey Key;

        public bool IsSystem { get { return Key.IsSystemTargetKey; } }

        public static readonly ActivationId Zero;

        private static readonly Interner<UniqueKey, ActivationId> interner;

        static ActivationId()
        {
            interner = new Interner<UniqueKey, ActivationId>(InternerConstants.SIZE_LARGE, InternerConstants.DefaultCacheCleanupFreq);
            Zero = FindOrCreate(UniqueKey.Empty);
        }

        /// <summary>
        /// Only used in Json serialization
        /// DO NOT USE TO CREATE A RANDOM ACTIVATION ID
        /// Use ActivationId.NewId to create new activation IDs.
        /// </summary>
        public ActivationId()
        {
        }

        private ActivationId(UniqueKey key)
        {
            this.Key = key;
        }

        public static ActivationId NewId()
        {
            return FindOrCreate(UniqueKey.NewKey());
        }

        // No need to encode SiloAddress in the activation address for system target. 
        // System targets have unique grain ids and addressed to a concrete silo, so in fact we don't need ActivationId at all for System targets.
        // Need to remove it all together. For now, just use grain id as activation id.
        public static ActivationId GetDeterministic(GrainId grain)
        {
            return FindOrCreate(((LegacyGrainId)grain).Key);
        }

        internal static ActivationId GetActivationId(UniqueKey key)
        {
            return FindOrCreate(key);
        }

        private static ActivationId FindOrCreate(UniqueKey key)
        {
            return interner.FindOrCreate(key, k => new ActivationId(k));
        }

        public override bool Equals(object obj)
        {
            var o = obj as ActivationId;
            return o != null && Key.Equals(o.Key);
        }

        public bool Equals(ActivationId other)
        {
            return other != null && Key.Equals(other.Key);
        }

        public override int GetHashCode()
        {
            return Key.GetHashCode();
        }

        public override string ToString()
        {
            string idString = Key.ToString().Substring(24, 8);
            return String.Format("@{0}{1}", IsSystem ? "S" : "", idString);
        }

        public string ToFullString()
        {
            string idString = Key.ToString();
            return String.Format("@{0}{1}", IsSystem ? "S" : "", idString);
        }
    }
}
