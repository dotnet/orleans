using Orleans.Concurrency;
using System;

namespace ProtoBuf.Serialization.Tests
{
    [ProtoContract]
    public class OtherPerson
    {
        [ProtoMember(1)]
        public int Id { get; set; }
        [ProtoMember(2)]
        public string Name { get; set; }
        [ProtoMember(3)]
        public Address Address { get; set; }
    }

    [Immutable]
    [ProtoContract]
    public class ImmutablePerson
    {
        [ProtoMember(1)]
        public int Id { get; set; }
        [ProtoMember(2)]
        public string Name { get; set; }
        [ProtoMember(3)]
        public Address Address { get; set; }
    }

    [ProtoContract]
    public class Address
    {
        public Address()
        {
            Created = DateTime.UtcNow;
        }

        [ProtoMember(1)]
        public string Line1 { get; set; }
        [ProtoMember(2)]
        public string Line2 { get; set; }
        [ProtoMember(3)]
        public DateTime Created { get; set; }
    }
}
