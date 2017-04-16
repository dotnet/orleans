using System;

namespace Orleans.Core
{
    public interface IGrainIdentity
    {
        Guid PrimaryKey { get; }

        long PrimaryKeyLong { get; }

        string PrimaryKeyString { get; }

        string IdentityString { get; }

        bool IsClient { get; }

        int TypeCode { get; }

        long GetPrimaryKeyLong(out string keyExt);

        Guid GetPrimaryKey(out string keyExt);

        uint GetUniformHashCode();
    }
}
