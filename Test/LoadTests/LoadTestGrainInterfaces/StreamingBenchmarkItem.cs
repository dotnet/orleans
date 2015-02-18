using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LoadTestGrainInterfaces
{
    [Serializable]
    // we use a struct here because (a) it's performance sensitive and (b) it's semantics are immutable, which // should be safe. if that changes, a struct would no longer be appropriate.
    public struct StreamingBenchmarkItem
    {
        private readonly int _data;
        private readonly Guid _streamGuid;

        public StreamingBenchmarkItem(int data, Guid streamGuid)
        {
            _data = data;
            _streamGuid = streamGuid;
        }

        public int Data { get { return _data; } }
        public Guid StreamGuid { get { return _streamGuid; } }

        public override string ToString()
        {
            return String.Format("((StreamBenchmarkItem){0})", Data);
        }
    }
}
