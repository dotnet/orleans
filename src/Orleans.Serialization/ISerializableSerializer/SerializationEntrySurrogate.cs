using System;

namespace Orleans.Serialization
{
    [GenerateSerializer]
    internal struct SerializationEntrySurrogate
    {
        [Id(0)]
        public string Name;

        [Id(1)]
        public object Value;

        [Id(2)]
        public Type ObjectType;
    }
}