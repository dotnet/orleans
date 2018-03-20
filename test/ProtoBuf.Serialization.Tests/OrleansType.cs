using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProtoBuf.Serialization.Tests
{
    [Serializable]
    public class OrleansType
    {
        public int val = 33;

        public string val2 = "Hello, world!";

        public int[] val3 = new[] { 1, 2 };

        public override bool Equals(object obj)
        {
            var o = obj as OrleansType;
            return o != null && val.Equals(o.val);
        }

        public override int GetHashCode()
        {
            return val;
        }
    }
}
