using System;
#if NET9_0_OR_GREATER
using System.Diagnostics.CodeAnalysis;
#endif

namespace Orleans.Serialization;

internal static class SerializerRuntimeFeatures
{
    internal const string GeneratedContextsSwitch = "Orleans.Serialization.UseGeneratedSerializerContexts";

#if NET9_0_OR_GREATER
    [FeatureSwitchDefinition(GeneratedContextsSwitch)]
#endif
    internal static bool UseGeneratedSerializerContexts { get; } =
        AppContext.TryGetSwitch(GeneratedContextsSwitch, out var enabled) && enabled;
}
