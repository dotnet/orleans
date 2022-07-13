using System;

namespace Orleans.Runtime
{
    [GenerateSerializer]
    internal sealed class RejectionResponse
    {
        [Id(0)]
        public string RejectionInfo { get; set; }

        [Id(1)]
        public Message.RejectionTypes RejectionType { get; set; }

        [Id(2)]
        public Exception Exception { get; set; }
    }
}
