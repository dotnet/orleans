using System;


namespace Orleans.Runtime
{
    internal interface IRingIdentifier<T> : IEquatable<T>
    {
        uint GetUniformHashCode();
    }
}
