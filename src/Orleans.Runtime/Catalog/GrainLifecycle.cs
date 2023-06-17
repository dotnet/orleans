
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal class GrainLifecycle : LifecycleSubject, IGrainLifecycle
    {
        private static readonly ImmutableDictionary<int, string> StageNames = GetStageNames(typeof(GrainLifecycleStage));
        private List<IGrainMigrationParticipant> _migrationParticipants;

        public GrainLifecycle(ILogger logger) : base(logger)
        {
        }

        public IEnumerable<IGrainMigrationParticipant> GetMigrationParticipants() => _migrationParticipants ?? (IEnumerable<IGrainMigrationParticipant>)Array.Empty<IGrainMigrationParticipant>();

        public void AddMigrationParticipant(IGrainMigrationParticipant participant)
        {
            lock (this)
            {
                _migrationParticipants ??= new();
                _migrationParticipants.Add(participant);
            }
        }

        public void RemoveMigrationParticipant(IGrainMigrationParticipant participant)
        {
            lock (this)
            {
                if (_migrationParticipants is null) return;
                _migrationParticipants.Remove(participant);
            }
        }

        protected override string GetStageName(int stage)
        {
            if (StageNames.TryGetValue(stage, out var result)) return result;
            return base.GetStageName(stage);
        }
    }
}
