using System;
using Orleans.Runtime;

namespace Orleans.Core
{
    internal class GrainIdentity : IGrainIdentity
    {
        private readonly IActivationData activationData;

        public GrainIdentity(IActivationData activationData)
        {
            this.activationData = activationData;
        }

        public Guid PrimaryKey
        {
            get { return activationData.Identity.GetPrimaryKey(); }
        }

        public long PrimaryKeyLong
        {
            get { return activationData.Identity.GetPrimaryKeyLong(); }
        }

        public string PrimaryKeyString
        {
            get { return activationData.Identity.GetPrimaryKeyString(); }
        }

        public string IdentityString
        {
            get { return activationData.IdentityString; }
        }

        public long GetPrimaryKeyLong(out string keyExt)
        {
            return activationData.Identity.GetPrimaryKeyLong(out keyExt);
        }

        public Guid GetPrimaryKey(out string keyExt)
        {
            return activationData.Identity.GetPrimaryKey(out keyExt);
        }
    }
}