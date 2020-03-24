using System;

namespace Orleans.Persistence.S3 {
    internal static class GrainStateExtensions
    {
        public static object CreateDefaultState(this IGrainState grainState) => Activator.CreateInstance(grainState.Type);
    }
}