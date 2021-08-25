
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal class GrainLifecycle : LifecycleSubject, IGrainLifecycle
    {
        private static readonly ImmutableDictionary<int, string> StageNames = GetStageNames(typeof(GrainLifecycleStage));

        public GrainLifecycle(ILogger logger) : base(logger)
        {
        }

        protected override string GetStageName(int stage)
        {
            if (StageNames.TryGetValue(stage, out var result)) return result;
            return base.GetStageName(stage);
        }
    }
}
