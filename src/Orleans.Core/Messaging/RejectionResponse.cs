using System;

namespace Orleans.Runtime
{
    [Id(102), GenerateSerializer, Immutable]
    internal sealed class RejectionResponse
    {
        [Id(0)]
        public string RejectionInfo { get; init; }

        [Id(1)]
        public Message.RejectionTypes RejectionType { get; init; }

        [Id(2)]
        public Exception Exception { get; init; }
    }
}
