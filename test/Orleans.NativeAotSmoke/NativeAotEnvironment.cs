using System;
using System.Runtime.CompilerServices;

namespace Orleans.NativeAotSmoke;

internal static class NativeAotEnvironment
{
    [ModuleInitializer]
    internal static void Validate()
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new InvalidOperationException("NativeAOT smoke scenarios require runtime code generation to be disabled.");
        }
    }
}
