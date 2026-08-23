using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Providers;
using Orleans.Runtime;

namespace Orleans
{
    internal interface ISiloControl : ISystemTarget, IVersionManager
    {
        [Alias("1422B0B7")] Task Ping(string message, CancellationToken cancellationToken = default);

        [Alias("F388CED1")] Task ForceGarbageCollection(CancellationToken cancellationToken = default);
        [Alias("45D07D09")] Task ForceActivationCollection(TimeSpan ageLimit, CancellationToken cancellationToken = default);
        [Alias("0C7DBD0C")] Task ForceRuntimeStatisticsCollection(CancellationToken cancellationToken = default);

        [Alias("F18EAF24")] Task<SiloRuntimeStatistics> GetRuntimeStatistics(CancellationToken cancellationToken = default);
        [Alias("FF707A30")] Task<List<Tuple<GrainId, string, int>>> GetGrainStatistics(CancellationToken cancellationToken = default);
        [Alias("B0F4C24B")] Task<List<DetailedGrainStatistic>> GetDetailedGrainStatistics(string[]? types = null, CancellationToken cancellationToken = default);
        [Alias("6DE16EF7")] Task<SimpleGrainStatistic[]> GetSimpleGrainStatistics(CancellationToken cancellationToken = default);
        [Alias("45172562")] Task<DetailedGrainReport> GetDetailedGrainReport(GrainId grainId, CancellationToken cancellationToken = default);

        [Alias("C4C370A5")] Task<int> GetActivationCount(CancellationToken cancellationToken = default);
        [Alias("E8327F0B")] Task MigrateRandomActivations(SiloAddress target, int count, CancellationToken cancellationToken = default);

        [Alias("355CA3FA")] Task<object?> SendControlCommandToProvider<T>(string providerName, int command, object? arg, CancellationToken cancellationToken = default) where T : IControllable;
        [Alias("85797C87")] Task<List<GrainId>> GetActiveGrains(GrainType grainType, CancellationToken cancellationToken = default);
        [OneWay, AlwaysInterleave]
        [Alias("16D39D91")] Task CompleteGatewayRequest(GrainId clientId, GrainId sourceId, CorrelationId correlationId, CancellationToken cancellationToken = default);
        [Alias("B99FB859")] Task DropDisconnectedClients(bool excludeRecent, CancellationToken cancellationToken = default);
    }
}
