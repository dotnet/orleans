using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Core
{
    public class GrainIdentifier : IGrainIdentifier
    {
        private readonly Grain _grain;

        public GrainIdentifier(Grain grain)
        {
            _grain = grain;
        }

        public Guid AsGuid()
        {
            return _grain.AsReference<IGrain>().GetPrimaryKey();
        }

        public long AsLong()
        {
            return _grain.AsReference<IGrainWithIntegerKey>().GetPrimaryKeyLong();
        }

        public string AsString()
        {
            return _grain.AsReference<IGrainWithStringKey>().GetPrimaryKeyString();
        }
    }
}
