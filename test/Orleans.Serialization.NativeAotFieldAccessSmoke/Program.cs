using System.Runtime.CompilerServices;
using Orleans.Serialization.NativeAotFieldAccessSmoke;

if (RuntimeFeature.IsDynamicCodeSupported)
{
    throw new InvalidOperationException("Run the published NativeAOT executable to validate native field access.");
}

if (args.Length == 0)
{
    NativeFieldAccessChecks.Run();
    Console.WriteLine("NativeAOT generated codecs and copiers restored private and backing fields.");
}
else if (args is ["--full-pipeline"])
{
    FieldAccessChecks.PrivateAndBackingFieldsRoundTripAndCopy();
    FieldAccessChecks.ConstrainedGenericFieldsRoundTripAndCopy();
    FieldAccessChecks.GenericStructFieldsRoundTripAndCopy();
    FieldAccessChecks.NestedGenericFieldsRoundTripAndCopy();
    Console.WriteLine("NativeAOT AddSerializer private and backing field serialization and copying passed.");
}
else
{
    throw new ArgumentException("Use no arguments for generated component checks or --full-pipeline for AddSerializer checks.");
}
