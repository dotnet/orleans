using System.Threading.Tasks;

namespace Orleans.Serialization.UnitTests.System;

[GenerateSerializer]
internal class Serializable
{
}

interface Invokable : IMyInvokableBaseType {
    Task MyMethod();
}
