using System;

namespace Orleans.Serialization.Configuration;

internal readonly record struct SerializationContract(
    Type ContractType,
    Type? TargetType,
    Type? SurrogateType,
    SerializationType? SurrogateDescription = null,
    SerializationType? TargetDescription = null);
