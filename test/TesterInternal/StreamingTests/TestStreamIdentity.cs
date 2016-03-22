using System;
using Orleans.Streams;

namespace UnitTests.StreamingTests
{
    internal class TestStreamIdentity : IStreamIdentity
    {
        public Guid Guid { get; set; }
        public string Namespace { get; set; }
    }
}
