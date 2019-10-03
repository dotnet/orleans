using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Runtime
{
    [Serializable]
    internal class TraceContext
    {
        public Guid ActivityId { get; set; }
    }
}
