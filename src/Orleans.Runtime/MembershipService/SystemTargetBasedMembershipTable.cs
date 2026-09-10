using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;
using Orleans.Configuration;
using Orleans.Serialization;

namespace Orleans.Runtime.MembershipService
{
    internal partial class SystemTargetBasedMembershipTable : IMembershipTable
    {
        private readonly IServiceProvider serviceProvider;
        private readonly ILogger logger;
        private IMembershipTableSystemTarget grain = null!;

        public SystemTargetBasedMembershipTable(IServiceProvider serviceProvider, ILogger<SystemTargetBasedMembershipTable> logger)
        {
            this.serviceProvider = serviceProvider;
            this.logger = logger;
        }
        [Obsolete("Use the overload accepting a CancellationToken instead.")]
        public Task InitializeMembershipTable(bool tryInitTableVersion) => InitializeMembershipTable(tryInitTableVersion, CancellationToken.None);

        public async Task InitializeMembershipTable(bool tryInitTableVersion, CancellationToken cancellationToken = default)
        {
            this.grain = await GetMembershipTable(cancellationToken);
        }

        private async Task<IMembershipTableSystemTarget> GetMembershipTable(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var options = this.serviceProvider.GetRequiredService<IOptions<DevelopmentClusterMembershipOptions>>().Value;
            if (options.PrimarySiloEndpoint == null)
            {
                throw new OrleansConfigurationException(
                    $"{nameof(DevelopmentClusterMembershipOptions)}.{nameof(options.PrimarySiloEndpoint)} must be set when using development clustering.");
            }

            var siloDetails = this.serviceProvider.GetService<ILocalSiloDetails>()!;
            bool isPrimarySilo = siloDetails.SiloAddress.Endpoint.Equals(options.PrimarySiloEndpoint);
            var grainFactory = this.serviceProvider.GetRequiredService<IInternalGrainFactory>();
            var result = grainFactory.GetSystemTarget<IMembershipTableSystemTarget>(Constants.SystemMembershipTableType, SiloAddress.New(options.PrimarySiloEndpoint, 0));
            if (isPrimarySilo)
            {
                await this.WaitForTableGrainToInit(result, cancellationToken);
            }

            return result;
        }

        // Only used with MembershipTableGrain to wait for primary to start.
        private async Task WaitForTableGrainToInit(IMembershipTableSystemTarget membershipTableSystemTarget, CancellationToken cancellationToken)
        {
            var timespan = Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(5);
            // This is a quick temporary solution to enable primary node to start fully before secondaries.
            // Secondary silos waits untill GrainBasedMembershipTable is created.
            for (int i = 0; i < 100; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var timeout = new CancellationTokenSource(timespan);
                using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                try
                {
                    await membershipTableSystemTarget.ReadAll(requestCancellation.Token);
                    LogInformationConnectedToMembershipTableProvider(logger);
                    return;
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    LogInformationWaitingForMembershipTableProvider(logger, timespan);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exc)
                {
                    var type = exc.GetBaseException().GetType();
                    if (type == typeof(TimeoutException) || type == typeof(OrleansException))
                    {
                        LogInformationWaitingForMembershipTableProvider(logger, timespan);
                    }
                    else
                    {
                        LogInformationMembershipTableProviderFailedToInitialize(logger);
                        throw;
                    }
                }

                await Task.Delay(timespan, cancellationToken);
            }
        }

        [Obsolete("Use the overload accepting a CancellationToken instead.")]
        public Task DeleteMembershipTableEntries(string clusterId) => DeleteMembershipTableEntries(clusterId, CancellationToken.None);

        public Task DeleteMembershipTableEntries(string clusterId, CancellationToken cancellationToken = default) => this.grain.DeleteMembershipTableEntries(clusterId, cancellationToken);

        [Obsolete("Use the overload accepting a CancellationToken instead.")]
        public Task<MembershipTableData> ReadRow(SiloAddress key) => ReadRow(key, CancellationToken.None);

        public Task<MembershipTableData> ReadRow(SiloAddress key, CancellationToken cancellationToken = default) => this.grain.ReadRow(key, cancellationToken);

        [Obsolete("Use the overload accepting a CancellationToken instead.")]
        public Task<MembershipTableData> ReadAll() => ReadAll(CancellationToken.None);

        public Task<MembershipTableData> ReadAll(CancellationToken cancellationToken = default) => this.grain.ReadAll(cancellationToken);

        [Obsolete("Use the overload accepting a CancellationToken instead.")]
        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion) => InsertRow(entry, tableVersion, CancellationToken.None);

        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion, CancellationToken cancellationToken = default) => this.grain.InsertRow(entry, tableVersion, cancellationToken);

        [Obsolete("Use the overload accepting a CancellationToken instead.")]
        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion) => UpdateRow(entry, etag, tableVersion, CancellationToken.None);

        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion, CancellationToken cancellationToken = default) => this.grain.UpdateRow(entry, etag, tableVersion, cancellationToken);

        [Obsolete("Use the overload accepting a CancellationToken instead.")]
        public Task UpdateIAmAlive(MembershipEntry entry) => UpdateIAmAlive(entry, CancellationToken.None);

        public Task UpdateIAmAlive(MembershipEntry entry, CancellationToken cancellationToken = default) => this.grain.UpdateIAmAlive(entry, cancellationToken);

        [Obsolete("Use the overload accepting a CancellationToken instead.")]
        public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) => CleanupDefunctSiloEntries(beforeDate, CancellationToken.None);

        public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate, CancellationToken cancellationToken = default) => this.grain.CleanupDefunctSiloEntries(beforeDate, cancellationToken);

        [LoggerMessage(
            EventId = (int)ErrorCode.MembershipFactory1,
            Level = LogLevel.Information,
            Message = "Creating in-memory membership table"
        )]
        private static partial void LogInformationCreatingInMemoryMembershipTable(ILogger logger);

        [LoggerMessage(
            EventId = (int)ErrorCode.MembershipTableGrainInit2,
            Level = LogLevel.Information,
            Message = "Connected to membership table provider."
        )]
        private static partial void LogInformationConnectedToMembershipTableProvider(ILogger logger);

        [LoggerMessage(
            EventId = (int)ErrorCode.MembershipTableGrainInit3,
            Level = LogLevel.Information,
            Message = "Waiting for membership table provider to initialize. Going to sleep for {Duration} and re-try to reconnect."
        )]
        private static partial void LogInformationWaitingForMembershipTableProvider(ILogger logger, TimeSpan duration);

        [LoggerMessage(
            EventId = (int)ErrorCode.MembershipTableGrainInit4,
            Level = LogLevel.Information,
            Message = "Membership table provider failed to initialize. Giving up."
        )]
        private static partial void LogInformationMembershipTableProviderFailedToInitialize(ILogger logger);
    }

    [Reentrant]
    internal sealed partial class MembershipTableSystemTarget : SystemTarget, IMembershipTableSystemTarget, ILifecycleParticipant<ISiloLifecycle>
    {
        private InMemoryMembershipTable table;
        private readonly ILogger logger;

        public MembershipTableSystemTarget(
            ILogger<MembershipTableSystemTarget> logger,
            DeepCopier deepCopier,
            SystemTargetShared shared)
            : base(CreateId(shared.SiloAddress), shared)
        {
            this.logger = logger;
            table = new InMemoryMembershipTable(deepCopier);
            LogInformationGrainBasedMembershipTableActivated(logger);
            shared.ActivationDirectory.RecordNewTarget(this);
        }

        private static SystemTargetGrainId CreateId(SiloAddress siloAddress)
        {
            return SystemTargetGrainId.Create(Constants.SystemMembershipTableType, SiloAddress.New(siloAddress.Endpoint, 0));
        }

        [Obsolete("Use the overload accepting a CancellationToken instead.")]
        public Task InitializeMembershipTable(bool tryInitTableVersion) => InitializeMembershipTable(tryInitTableVersion, CancellationToken.None);

        public Task InitializeMembershipTable(bool tryInitTableVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogInformationInitializeMembershipTable(logger, tryInitTableVersion);
            return Task.CompletedTask;
        }

        [Obsolete("Use the overload accepting a CancellationToken instead.")]
        public Task DeleteMembershipTableEntries(string clusterId) => DeleteMembershipTableEntries(clusterId, CancellationToken.None);

        public Task DeleteMembershipTableEntries(string clusterId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogInformationDeleteMembershipTableEntries(logger, clusterId);
            table = null!;
            return Task.CompletedTask;
        }

        [Obsolete("Use the overload accepting a CancellationToken instead.")]
        public Task<MembershipTableData> ReadRow(SiloAddress key) => ReadRow(key, CancellationToken.None);

        public Task<MembershipTableData> ReadRow(SiloAddress key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(table.Read(key));
        }

        [Obsolete("Use the overload accepting a CancellationToken instead.")]
        public Task<MembershipTableData> ReadAll() => ReadAll(CancellationToken.None);

        public Task<MembershipTableData> ReadAll(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var t = table.ReadAll();
            return Task.FromResult(t);
        }

        [Obsolete("Use the overload accepting a CancellationToken instead.")]
        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion) => InsertRow(entry, tableVersion, CancellationToken.None);

        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogDebugInsertRow(logger, entry, tableVersion);
            bool result = table.Insert(entry, tableVersion);
            if (result == false)
                LogInformationInsertRowFailed(logger, entry, tableVersion, table.ReadAll());

            return Task.FromResult(result);
        }

        [Obsolete("Use the overload accepting a CancellationToken instead.")]
        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion) => UpdateRow(entry, etag, tableVersion, CancellationToken.None);

        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogDebugUpdateRow(logger, entry, etag, tableVersion);
            bool result = table.Update(entry, etag, tableVersion);
            if (result == false)
                LogInformationUpdateRowFailed(logger, entry, etag, tableVersion, table.ReadAll());

            return Task.FromResult(result);
        }

        [Obsolete("Use the overload accepting a CancellationToken instead.")]
        public Task UpdateIAmAlive(MembershipEntry entry) => UpdateIAmAlive(entry, CancellationToken.None);

        public Task UpdateIAmAlive(MembershipEntry entry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogDebugUpdateIAmAlive(logger, entry);
            table.UpdateIAmAlive(entry);
            return Task.CompletedTask;
        }

        [Obsolete("Use the overload accepting a CancellationToken instead.")]
        public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) => CleanupDefunctSiloEntries(beforeDate, CancellationToken.None);

        public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            table.CleanupDefunctSiloEntries(beforeDate);
            return Task.CompletedTask;
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            // Do nothing, just ensure that this instance is created so that it can register itself in the catalog.
        }

        [LoggerMessage(
            EventId = (int)ErrorCode.MembershipGrainBasedTable1,
            Level = LogLevel.Information,
            Message = "GrainBasedMembershipTable Activated."
        )]
        private static partial void LogInformationGrainBasedMembershipTableActivated(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "InitializeMembershipTable {TryInitTableVersion}."
        )]
        private static partial void LogInformationInitializeMembershipTable(ILogger logger, bool tryInitTableVersion);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "DeleteMembershipTableEntries {ClusterId}"
        )]
        private static partial void LogInformationDeleteMembershipTableEntries(ILogger logger, string clusterId);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "InsertRow entry = {Entry}, table version = {Version}"
        )]
        private static partial void LogDebugInsertRow(ILogger logger, MembershipEntry entry, TableVersion version);

        [LoggerMessage(
            EventId = (int)ErrorCode.MembershipGrainBasedTable2,
            Level = LogLevel.Information,
            Message = "Insert of {Entry} and table version {Version} failed. Table now is {Table}"
        )]
        private static partial void LogInformationInsertRowFailed(ILogger logger, MembershipEntry entry, TableVersion version, MembershipTableData table);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "UpdateRow entry = {Entry}, etag = {ETag}, table version = {Version}"
        )]
        private static partial void LogDebugUpdateRow(ILogger logger, MembershipEntry entry, string etag, TableVersion version);

        [LoggerMessage(
            EventId = (int)ErrorCode.MembershipGrainBasedTable3,
            Level = LogLevel.Information,
            Message = "Update of {Entry}, eTag {ETag}, table version {Version} failed. Table now is {Table}"
        )]
        private static partial void LogInformationUpdateRowFailed(ILogger logger, MembershipEntry entry, string etag, TableVersion version, MembershipTableData table);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "UpdateIAmAlive entry = {Entry}"
        )]
        private static partial void LogDebugUpdateIAmAlive(ILogger logger, MembershipEntry entry);
    }
}
