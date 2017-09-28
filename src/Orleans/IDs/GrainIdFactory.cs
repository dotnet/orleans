using System;
using Orleans.Serialization;

namespace Orleans.Runtime
{
    internal static class GrainIdFactory
    {
        private const int INTERN_CACHE_INITIAL_SIZE = InternerConstants.SIZE_LARGE;
        private static readonly object lockable = new object();
        private static readonly TimeSpan internCacheCleanupInterval = InternerConstants.DefaultCacheCleanupFreq;
        private static Interner<UniqueKey, GrainId> grainIdInternCache;

        public static GrainId NewId()
        {
            return FindOrCreateGrainId(UniqueKey.NewKey(Guid.NewGuid(), UniqueKey.Category.Grain));
        }

        public static GrainId NewClientId(string clusterId = null)
        {
            return NewClientId(Guid.NewGuid(), clusterId);
        }

        internal static GrainId NewClientId(Guid id, string clusterId = null)
        {
            return FindOrCreateGrainId(UniqueKey.NewKey(id,
                clusterId == null ? UniqueKey.Category.Client : UniqueKey.Category.GeoClient, 0, clusterId));
        }

        internal static GrainId GetGrainId(UniqueKey key)
        {
            return FindOrCreateGrainId(key);
        }

        internal static GrainId GetSystemGrainId(Guid guid)
        {
            return FindOrCreateGrainId(UniqueKey.NewKey(guid, UniqueKey.Category.SystemGrain));
        }

        internal static GrainId GetGrainIdForTesting(Guid guid)
        {
            return FindOrCreateGrainId(UniqueKey.NewKey(guid, UniqueKey.Category.None));
        }

        internal static GrainId NewSystemTargetGrainIdByTypeCode(int typeData)
        {
            return FindOrCreateGrainId(UniqueKey.NewSystemTargetKey(Guid.NewGuid(), typeData));
        }

        internal static GrainId GetSystemTargetGrainId(short systemGrainId)
        {
            return FindOrCreateGrainId(UniqueKey.NewSystemTargetKey(systemGrainId));
        }

        internal static GrainId GetGrainId(long typeCode, long primaryKey, string keyExt=null)
        {
            return FindOrCreateGrainId(UniqueKey.NewKey(primaryKey, 
                keyExt == null ? UniqueKey.Category.Grain : UniqueKey.Category.KeyExtGrain, 
                typeCode, keyExt));
        }

        internal static GrainId GetGrainId(long typeCode, Guid primaryKey, string keyExt=null)
        {
            return FindOrCreateGrainId(UniqueKey.NewKey(primaryKey, 
                keyExt == null ? UniqueKey.Category.Grain : UniqueKey.Category.KeyExtGrain, 
                typeCode, keyExt));
        }

        internal static GrainId GetGrainId(long typeCode, string primaryKey)
        {
            return FindOrCreateGrainId(UniqueKey.NewKey(0L,
                UniqueKey.Category.KeyExtGrain,
                typeCode, primaryKey));
        }

        internal static GrainId GetGrainServiceGrainId(short id, int typeData)
        {
            return FindOrCreateGrainId(UniqueKey.NewGrainServiceKey(id, typeData));
        }

        private static GrainId FindOrCreateGrainId(UniqueKey key)
        {
            // Note: This is done here to avoid a wierd cyclic dependency / static initialization ordering problem involving the GrainId, Constants & Interner classes
            if (grainIdInternCache != null) return grainIdInternCache.FindOrCreate(key, k => new GrainId(k));

            lock (lockable)
            {
                if (grainIdInternCache == null)
                {
                    grainIdInternCache = new Interner<UniqueKey, GrainId>(INTERN_CACHE_INITIAL_SIZE, internCacheCleanupInterval);
                }
            }
            return grainIdInternCache.FindOrCreate(key, k => new GrainId(k));
        }

        /// <summary>
        /// Create a new GrainId object by parsing string in a standard form returned from <c>ToParsableString</c> method.
        /// </summary>
        /// <param name="grainId">String containing the GrainId info to be parsed.</param>
        /// <returns>New GrainId object created from the input data.</returns>
        internal static GrainId FromParsableString(string grainId)
        {
            // NOTE: This function must be the "inverse" of ToParsableString, and data must round-trip reliably.

            var key = UniqueKey.Parse(grainId);
            return FindOrCreateGrainId(key);
        }

        internal static GrainId FromByteArray(byte[] byteArray)
        {
            var reader = new BinaryTokenStreamReader(byteArray);
            return reader.ReadGrainId();
        }

        internal static byte[] ToByteArray(this GrainId @this)
        {
            var writer = new BinaryTokenStreamWriter();
            writer.Write(@this);
            var result = writer.ToByteArray();
            writer.ReleaseBuffers();
            return result;
        }
    }
}