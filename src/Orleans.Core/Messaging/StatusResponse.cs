using System;
using System.Collections.Generic;
using System.Text;
using Orleans.Concurrency;

namespace Orleans.Runtime
{
    [Immutable]
    [Serializable]
    internal class StatusResponse
    {
        private readonly uint _statusFlags;

        public StatusResponse(bool isExecuting, bool isWaiting, List<string> diagnostics)
        {
            if (isExecuting) _statusFlags |= 0x1;
            if (isWaiting) _statusFlags |= 0x2;

            Diagnostics = diagnostics;
        }

        public List<string> Diagnostics { get; }

        public bool IsExecuting => (_statusFlags & 0x1) != 0;

        public bool IsWaiting => (_statusFlags & 0x2) != 0;

        public override string ToString() => $"IsExecuting: {IsExecuting}, IsWaiting: {IsWaiting}, Diagnostics: [{string.Join(", ", this.Diagnostics)}]";
    }
}
