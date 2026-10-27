using System;

namespace Orleans.Serialization.Configuration;

internal readonly record struct SerializationContract(
    Type ContractType,
    Type? TargetType,
    Type? SurrogateType,
    SerializationType? SurrogateDescription = null,
    SerializationType? TargetDescription = null,
    int? RegistrationOrder = null)
{
    internal bool IsEquivalentTo(SerializationContract other)
        => ContractType == other.ContractType
            && TargetType == other.TargetType
            && SurrogateType == other.SurrogateType
            && SerializationType.AreEquivalent(TargetDescription, other.TargetDescription)
            && SerializationType.AreEquivalent(SurrogateDescription, other.SurrogateDescription);
}
