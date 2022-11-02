using System;
using System.Collections.Generic;

namespace Orleans.Runtime
{
    [Id(103), Serializable, GenerateSerializer, Immutable]
    internal sealed class StatusResponse
    {
        [Id(0)]
        private readonly uint _statusFlags;

        public StatusResponse(bool isExecuting, bool isWaiting, List<string> diagnostics)
        {
            if (isExecuting) _statusFlags |= 0x1;
            if (isWaiting) _statusFlags |= 0x2;

            Diagnostics = diagnostics;
        }

        [Id(1)]
        public List<string> Diagnostics { get; }

        public bool IsExecuting => (_statusFlags & 0x1) != 0;

        public bool IsWaiting => (_statusFlags & 0x2) != 0;

        public override string ToString() => $"IsExecuting: {IsExecuting}, IsWaiting: {IsWaiting}, Diagnostics: [{string.Join(", ", this.Diagnostics)}]";
    }
}
