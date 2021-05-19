using System.Runtime.InteropServices;

namespace Orleans.Runtime
{
    internal static class PlatformRuntimeInfo
    {
        public static bool SupportsThreading { get; } = DoesPlatformSupportThreading();

        private static bool DoesPlatformSupportThreading()
        {
            switch (RuntimeInformation.ProcessArchitecture)
            {
                // Could make it as simple as Architecture.Wasm,
                // but this enum member does not exist in some frameworks,
                // hence I have to inverse the check.
                case Architecture.X86:
                case Architecture.X64:
                case Architecture.Arm:
                case Architecture.Arm64:
                    return true;
                default:
                    return false;
            }
        }
    }
}