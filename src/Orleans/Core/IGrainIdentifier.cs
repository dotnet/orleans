using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Core
{
    public interface IGrainIdentifier
    {
        Guid AsGuid();

        long AsLong();

        string AsString();
    }
}
