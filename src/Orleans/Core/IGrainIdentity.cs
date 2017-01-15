using System;

namespace Orleans.Core
{
    public interface IGrainIdentity
    {
        Guid PrimaryKey { get; }

        long PrimaryKeyLong { get; }

        string PrimaryKeyString { get; }

        string IdentityString { get; }

        long GetPrimaryKeyLong(out string keyExt);

        Guid GetPrimaryKey(out string keyExt);
    }
}
