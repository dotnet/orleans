using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Persistence.Cosmos
{
    internal readonly struct GrainId
    {
        public GrainId(string type, string key)
        {
            this.Type = type;
            this.Key = key;
        }

        public string Type { get; }

        public string Key { get; }

        public override string ToString() => $"{this.Type}/{this.Key}";
    }
}
