using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
