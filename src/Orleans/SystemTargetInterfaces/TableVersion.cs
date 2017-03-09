using System;
using Orleans.Concurrency;

namespace Orleans
{
    [Serializable]
    [Immutable]
    public class TableVersion
    {
        /// <summary>
        /// The version part of this TableVersion. Monotonically increasing number.
        /// </summary>
        public int Version { get; private set; }

        /// <summary>
        /// The etag of this TableVersion, used for validation of table update operations.
        /// </summary>
        public string VersionEtag { get; private set; }

        public TableVersion(int version, string eTag)
        {
            Version = version;
            VersionEtag = eTag;
        }

        public TableVersion Next()
        {
            return new TableVersion(Version + 1, VersionEtag);
        }

        public override string ToString()
        {
            return string.Format("<{0}, {1}>", Version, VersionEtag);
        }
    }
}