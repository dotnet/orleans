using System;
using Orleans.Runtime;

namespace Orleans.Core
{
    internal class GrainIdentity : IGrainIdentity
    {
        private readonly GrainId  grainId;

        public GrainIdentity(GrainId grainId)
        {
            this.grainId = grainId;
        }

        public Guid PrimaryKey
        {
            get { return grainId.GetPrimaryKey(); }
        }

        public long PrimaryKeyLong
        {
            get { return grainId.GetPrimaryKeyLong(); }
        }

        public string PrimaryKeyString
        {
            get { return grainId.GetPrimaryKeyString(); }
        }

        public string IdentityString
        {
            get { return grainId.ToDetailedString(); }
        }

        public long GetPrimaryKeyLong(out string keyExt)
        {
            return grainId.GetPrimaryKeyLong(out keyExt);
        }

        public Guid GetPrimaryKey(out string keyExt)
        {
            return grainId.GetPrimaryKey(out keyExt);
        }
    }
}