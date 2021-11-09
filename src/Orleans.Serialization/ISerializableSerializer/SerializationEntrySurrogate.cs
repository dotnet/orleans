using System;

namespace Orleans.Serialization
{
    [GenerateSerializer]
    internal struct SerializationEntrySurrogate
    {
        [Id(1)]
        public string Name { get; set; }

        [Id(2)]
        public object Value { get; set; }

        [Id(3)]
        public Type ObjectType { get; set; }
    }
}