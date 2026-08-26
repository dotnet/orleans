using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// Interface for Membership Table.
    /// </summary>
    public interface IMembershipTable
    {
        /// <summary>
        /// Initializes the membership table, will be called before all other methods
        /// </summary>
        /// <param name="tryInitTableVersion">whether an attempt will be made to init the underlying table</param>
        /// <returns>A task representing the initialization operation.</returns>
        Task InitializeMembershipTable(bool tryInitTableVersion);

        /// <summary>
        /// Deletes all table entries of the given clusterId
        /// </summary>
        /// <param name="clusterId">The identifier of the cluster whose entries are deleted.</param>
        /// <returns>A task representing the deletion operation.</returns>
        Task DeleteMembershipTableEntries(string clusterId);

        /// <summary>
        /// Delete all dead silo entries older than <paramref name="beforeDate"/>
        /// </summary>
        /// <param name="beforeDate">The exclusive upper bound for the last known update time of entries to delete.</param>
        /// <returns>A task representing the cleanup operation.</returns>
        Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate);

        /// <summary>
        /// Atomically reads the Membership Table information about a given silo.
        /// The returned MembershipTableData includes one MembershipEntry entry for a given silo and the 
        /// TableVersion for this table. The MembershipEntry and the TableVersion have to be read atomically.
        /// </summary>
        /// <param name="key">The address of the silo whose membership information needs to be read.</param>
        /// <returns>The membership information for a given silo: MembershipTableData consisting one MembershipEntry entry and
        /// TableVersion, read atomically.</returns>
        Task<MembershipTableData> ReadRow(SiloAddress key);

        /// <summary>
        /// Atomically reads the full content of the Membership Table.
        /// The returned MembershipTableData includes all MembershipEntry entry for all silos in the table and the 
        /// TableVersion for this table. The MembershipEntries and the TableVersion have to be read atomically.
        /// </summary>
        /// <returns>The membership information for a given table: MembershipTableData consisting multiple MembershipEntry entries and
        /// TableVersion, all read atomically.</returns>
        Task<MembershipTableData> ReadAll();

        /// <summary>
        /// Atomically tries to insert (add) a new MembershipEntry for one silo and also update the TableVersion.
        /// If operation succeeds, the following changes would be made to the table:
        /// 1) New MembershipEntry will be added to the table.
        /// 2) The newly added MembershipEntry will also be added with the new unique automatically generated eTag.
        /// 3) TableVersion.Version in the table will be updated to the new TableVersion.Version.
        /// 4) TableVersion etag in the table will be updated to the new unique automatically generated eTag.
        /// All those changes to the table, insert of a new row and update of the table version and the associated etags, should happen atomically, or fail atomically with no side effects.
        /// The operation should fail in each of the following conditions:
        /// 1) A MembershipEntry for a given silo already exist in the table
        /// 2) Update of the TableVersion failed since the given TableVersion etag (as specified by the TableVersion.VersionEtag property) did not match the TableVersion etag in the table.
        /// </summary>
        /// <param name="entry">MembershipEntry to be inserted.</param>
        /// <param name="tableVersion">The new TableVersion for this table, along with its etag.</param>
        /// <returns>True if the insert operation succeeded and false otherwise.</returns>
        Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion);

        /// <summary>
        /// Atomically tries to update the MembershipEntry for one silo and also update the TableVersion.
        /// If operation succeeds, the following changes would be made to the table:
        /// 1) The MembershipEntry for this silo will be updated to the new MembershipEntry (the old entry will be fully substituted by the new entry) 
        /// 2) The eTag for the updated MembershipEntry will also be eTag with the new unique automatically generated eTag.
        /// 3) TableVersion.Version in the table will be updated to the new TableVersion.Version.
        /// 4) TableVersion etag in the table will be updated to the new unique automatically generated eTag.
        /// All those changes to the table, update of a new row and update of the table version and the associated etags, should happen atomically, or fail atomically with no side effects.
        /// The operation should fail in each of the following conditions:
        /// 1) A MembershipEntry for a given silo does not exist in the table
        /// 2) A MembershipEntry for a given silo exist in the table but its etag in the table does not match the provided etag.
        /// 3) Update of the TableVersion failed since the given TableVersion etag (as specified by the TableVersion.VersionEtag property) did not match the TableVersion etag in the table.
        /// </summary>
        /// <param name="entry">MembershipEntry to be updated.</param>
        /// <param name="etag">The etag  for the given MembershipEntry.</param>
        /// <param name="tableVersion">The new TableVersion for this table, along with its etag.</param>
        /// <returns>True if the update operation succeeded and false otherwise.</returns>
        Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion);

        /// <summary>
        /// Updates the IAmAlive part (column) of the MembershipEntry for this silo.
        /// The operation can also restore this silo's immutable metadata when <see cref="MembershipEntry.Metadata"/>
        /// is available and the stored metadata is missing. It should not change other columns.
        /// This operation is a "dirty write" or "in place update" and is performed without etag validation. 
        /// With regards to eTags update:
        /// This operation may automatically update the eTag associated with the given silo row, but it does not have to. It can also leave the etag not changed ("dirty write").
        /// With regards to TableVersion:
        /// this operation should not change the TableVersion of the table. It should leave it untouched.
        /// There is no scenario where this operation could fail due to table semantical reasons. It can only fail due to network problems or table unavailability.
        /// </summary>
        /// <param name="entry">The membership entry containing the updated <see cref="MembershipEntry.IAmAliveTime"/> value.</param>
        /// <returns>A task representing the update operation.</returns>
        Task UpdateIAmAlive(MembershipEntry entry);
    }

    /// <summary>
    /// Membership table interface for system target based implementation.
    /// </summary>
    public interface IMembershipTableSystemTarget : IMembershipTable, ISystemTarget
    {
    }

    /// <summary>
    /// Represents the version of a membership table and the entity tag used for optimistic concurrency.
    /// </summary>
    [Serializable, GenerateSerializer, Immutable]
    public sealed class TableVersion : ISpanFormattable, IEquatable<TableVersion>
    {
        /// <summary>
        /// The version part of this TableVersion. Monotonically increasing number.
        /// </summary>
        [Id(0)]
        public int Version { get; }

        /// <summary>
        /// The etag of this TableVersion, used for validation of table update operations.
        /// </summary>
        [Id(1)]
        public string VersionEtag { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="TableVersion"/> class.
        /// </summary>
        /// <param name="version">The membership table version.</param>
        /// <param name="eTag">The entity tag used to validate updates to this version.</param>
        public TableVersion(int version, string eTag)
        {
            Version = version;
            VersionEtag = eTag;
        }

        /// <summary>
        /// Creates the next membership table version while retaining the current entity tag for concurrency validation.
        /// </summary>
        /// <returns>The next membership table version.</returns>
        public TableVersion Next() => new(Version + 1, VersionEtag);

        /// <inheritdoc />
        public override string ToString() => $"<{Version}, {VersionEtag}>";
        string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

        bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
            => destination.TryWrite($"<{Version}, {VersionEtag}>", out charsWritten);

        /// <inheritdoc />
        public override bool Equals(object? obj) => Equals(obj as TableVersion);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(Version, VersionEtag);

        /// <inheritdoc />
        public bool Equals(TableVersion? other) => other is not null && Version == other.Version && VersionEtag == other.VersionEtag;

        /// <summary>
        /// Determines whether two membership table versions are equal.
        /// </summary>
        /// <param name="left">The first membership table version to compare.</param>
        /// <param name="right">The second membership table version to compare.</param>
        /// <returns><see langword="true"/> if the versions and entity tags are equal; otherwise, <see langword="false"/>.</returns>
        public static bool operator ==(TableVersion? left, TableVersion? right) => EqualityComparer<TableVersion>.Default.Equals(left, right);

        /// <summary>
        /// Determines whether two membership table versions are not equal.
        /// </summary>
        /// <param name="left">The first membership table version to compare.</param>
        /// <param name="right">The second membership table version to compare.</param>
        /// <returns><see langword="true"/> if the versions or entity tags differ; otherwise, <see langword="false"/>.</returns>
        public static bool operator !=(TableVersion? left, TableVersion? right) => !(left == right);
    }

    /// <summary>
    /// Represents an atomic snapshot of membership entries and the membership table version.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class MembershipTableData
    {
        /// <summary>
        /// Gets the membership entries and their entity tags.
        /// </summary>
        [Id(0)]
        public IReadOnlyList<Tuple<MembershipEntry, string>> Members { get; private set; }

        /// <summary>
        /// Gets the version of the membership table snapshot.
        /// </summary>
        [Id(1)]
        public TableVersion Version { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="MembershipTableData"/> class from multiple membership rows.
        /// </summary>
        /// <param name="list">The membership entries and their entity tags.</param>
        /// <param name="version">The version of the membership table snapshot.</param>
        public MembershipTableData(List<Tuple<MembershipEntry, string>> list, TableVersion version)
        {
            // put deads at the end, just for logging.
            list.Sort(
               (x, y) =>
               {
                   if (x.Item1.Status == SiloStatus.Dead) return 1; // put Deads at the end
                   if (y.Item1.Status == SiloStatus.Dead) return -1; // put Deads at the end
                   return string.CompareOrdinal(x.Item1.SiloName, y.Item1.SiloName);
               });
            Members = list;
            Version = version;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MembershipTableData"/> class from one membership row.
        /// </summary>
        /// <param name="tuple">The membership entry and its entity tag.</param>
        /// <param name="version">The version of the membership table snapshot.</param>
        public MembershipTableData(Tuple<MembershipEntry, string> tuple, TableVersion version)
        {
            Members = new[] { tuple };
            Version = version;
        }

        /// <summary>
        /// Initializes an empty instance of the <see cref="MembershipTableData"/> class.
        /// </summary>
        /// <param name="version">The version of the membership table snapshot.</param>
        public MembershipTableData(TableVersion version)
        {
            Members = Array.Empty<Tuple<MembershipEntry, string>>();
            Version = version;
        }

        /// <summary>
        /// Finds the membership row for the specified silo.
        /// </summary>
        /// <param name="silo">The silo address to find.</param>
        /// <returns>The membership entry and entity tag, or <see langword="null"/> if the silo is not present.</returns>
        public Tuple<MembershipEntry, string>? TryGet(SiloAddress silo)
        {
            foreach (var item in Members)
                if (item.Item1.SiloAddress.Equals(silo))
                    return item;

            return null;
        }

        /// <inheritdoc />
        public override string ToString()
        {
            int active = Members.Count(e => e.Item1.Status == SiloStatus.Active);
            int dead = Members.Count(e => e.Item1.Status == SiloStatus.Dead);
            int created = Members.Count(e => e.Item1.Status == SiloStatus.Created);
            int joining = Members.Count(e => e.Item1.Status == SiloStatus.Joining);
            int shuttingDown = Members.Count(e => e.Item1.Status == SiloStatus.ShuttingDown);
            int stopping = Members.Count(e => e.Item1.Status == SiloStatus.Stopping);

            return @$"{Members.Count} silos, {active} are Active, {dead} are Dead{(created > 0 ? $", {created} are Created" : null)}{(joining > 0 ? $", {joining} are Joining" : null)}{(shuttingDown > 0 ? $", {shuttingDown} are ShuttingDown" : null)}{(stopping > 0 ? $", {stopping} are Stopping" : null)}, Version={Version}. All silos: {Utils.EnumerableToString(Members.Select(t => t.Item1))}";
        }

        /// <summary>
        /// Creates a membership table snapshot which retains only the latest dead silo generation for each endpoint.
        /// </summary>
        /// <returns>
        /// A membership table snapshot containing all non-dead entries and the latest dead entry for each endpoint.
        /// </returns>
        public MembershipTableData WithoutDuplicateDeads()
        {
            var dead = new Dictionary<IPEndPoint, Tuple<MembershipEntry, string>>();
            // pick only latest Dead for each instance
            foreach (var next in Members.Where(item => item.Item1.Status == SiloStatus.Dead))
            {
                var ipEndPoint = next.Item1.SiloAddress.Endpoint;
                Tuple<MembershipEntry, string>? prev;
                if (!dead.TryGetValue(ipEndPoint, out prev))
                {
                    dead[ipEndPoint] = next;
                }
                else
                {
                    // later dead.
                    if (next.Item1.SiloAddress.Generation.CompareTo(prev.Item1.SiloAddress.Generation) > 0)
                        dead[ipEndPoint] = next;
                }
            }

            //now add back non-dead
            List<Tuple<MembershipEntry, string>> all = dead.Values.ToList();
            all.AddRange(Members.Where(item => item.Item1.Status != SiloStatus.Dead));
            return new MembershipTableData(all, Version);
        }

        internal Dictionary<SiloAddress, SiloStatus> GetSiloStatuses(Func<SiloStatus, bool> filter, bool includeMyself, SiloAddress myAddress)
        {
            var result = new Dictionary<SiloAddress, SiloStatus>();
            foreach (var memberEntry in this.Members)
            {
                var entry = memberEntry.Item1;
                if (!includeMyself && entry.SiloAddress.Equals(myAddress)) continue;
                if (filter(entry.Status)) result[entry.SiloAddress] = entry.Status;
            }

            return result;
        }
    }

    /// <summary>
    /// Represents a silo's membership record.
    /// </summary>
    [GenerateSerializer]
    [Serializable]
    public sealed class MembershipEntry : ISpanFormattable
    {
        /// <summary>
        /// The silo unique identity (ip:port:epoch). Used mainly by the Membership Protocol.
        /// </summary>
        [Id(0)]
        public SiloAddress SiloAddress { get; set; } = default!;

        /// <summary>
        /// The silo status. Managed by the Membership Protocol.
        /// </summary>
        [Id(1)]
        public SiloStatus Status { get; set; }

        /// <summary>
        /// The list of silos that suspect this silo. Managed by the Membership Protocol.
        /// </summary>
        [Id(2)]
        public List<Tuple<SiloAddress, DateTime>>? SuspectTimes { get; set; }

        /// <summary>
        /// Silo to clients TCP port. Set on silo startup.
        /// </summary>    
        [Id(3)]
        public int ProxyPort { get; set; }

        /// <summary>
        /// The DNS host name of the silo. Equals to Dns.GetHostName(). Set on silo startup.
        /// </summary>
        [Id(4)]
        public string HostName { get; set; } = default!;

        /// <summary>
        /// the name of the specific silo instance within a cluster. 
        /// If running in Azure - the name of this role instance. Set on silo startup.
        /// </summary>
        [Id(5)]
        public string SiloName { get; set; } = default!;

        /// <summary>
        /// Gets or sets the role name associated with the silo.
        /// </summary>
        [Id(6)]
        public string? RoleName { get; set; } // Optional - only for Azure role

        /// <summary>
        /// Gets or sets the silo's update zone.
        /// </summary>
        [Id(7)]
        public int UpdateZone { get; set; }  // Optional - only for Azure role

        /// <summary>
        /// Gets or sets the silo's fault zone.
        /// </summary>
        [Id(8)]
        public int FaultZone { get; set; }   // Optional - only for Azure role

        /// <summary>
        /// Time this silo was started. For diagnostics and troubleshooting only.
        /// </summary>
        [Id(9)]
        public DateTime StartTime { get; set; }

        /// <summary>
        /// the last time this silo reported that it is alive. For diagnostics and troubleshooting only.
        /// </summary>
        [Id(10)]
        public DateTime IAmAliveTime { get; set; }

        /// <summary>
        /// Gets or sets the silo metadata, if it is available from the membership table.
        /// </summary>
        /// <remarks>
        /// A <see langword="null"/> value indicates that metadata is unavailable. An empty dictionary indicates
        /// that metadata is available and the silo has not supplied any metadata values.
        /// Metadata is fixed for the lifetime of a silo instance. Membership snapshots can enrich an unavailable
        /// value when metadata becomes available, and retain the first available value thereafter. During a
        /// mixed-version rolling upgrade, an older replace-style provider write can temporarily remove inline
        /// metadata. An active metadata-aware silo restores its own metadata on its next heartbeat.
        /// Storage limits are determined by the configured membership provider.
        /// </remarks>
        [Id(11)]
        public ImmutableDictionary<string, string>? Metadata { get; set; }

        internal DateTime EffectiveIAmAliveTime
        {
            get
            {
                var startTimeUtc = DateTime.SpecifyKind(StartTime, DateTimeKind.Utc);
                var iAmAliveTimeUtc = DateTime.SpecifyKind(IAmAliveTime, DateTimeKind.Utc);
                return startTimeUtc > iAmAliveTimeUtc ? startTimeUtc : iAmAliveTimeUtc;
            }
        }

        /// <summary>
        /// Gets the most recent time at which this entry is known to have been updated. This is the later of
        /// <see cref="EffectiveIAmAliveTime"/> and the most recent time at which a silo voted to suspect this
        /// silo (see <see cref="SuspectTimes"/>). Declaring a silo dead records a suspect vote, so this value
        /// reflects a recent death declaration even when the silo last reported itself alive long ago. It is
        /// used to retain the most-recently-updated defunct entries when cleaning up the membership table, so
        /// that recently-declared-dead silos remain visible in membership snapshots.
        /// </summary>
        internal DateTime EffectiveUpdateTime
        {
            get
            {
                var result = EffectiveIAmAliveTime;
                if (SuspectTimes is { } suspectTimes)
                {
                    foreach (var vote in suspectTimes)
                    {
                        var voteTimeUtc = DateTime.SpecifyKind(vote.Item2, DateTimeKind.Utc);
                        if (voteTimeUtc > result)
                        {
                            result = voteTimeUtc;
                        }
                    }
                }

                return result;
            }
        }

        /// <summary>
        /// Adds or updates a silo's vote to declare this silo unavailable.
        /// </summary>
        /// <param name="localSilo">The silo casting the vote.</param>
        /// <param name="voteTime">The time at which the vote was cast.</param>
        /// <param name="maxVotes">The maximum number of votes to retain.</param>
        public void AddOrUpdateSuspector(SiloAddress localSilo, DateTime voteTime, int maxVotes)
        {
            var allVotes = SuspectTimes ??= new List<Tuple<SiloAddress, DateTime>>();

            // Find voting place:
            //      update my vote, if I voted previously
            //      OR if the list is not full - just add a new vote
            //      OR overwrite the oldest entry.
            int indexToWrite = allVotes.FindIndex(voter => localSilo.Equals(voter.Item1));
            if (indexToWrite == -1)
            {
                // My vote is not recorded. Find the most outdated vote if the list is full, and overwrite it
                if (allVotes.Count >= maxVotes) // if the list is full
                {
                    // The list is full, so pick the most outdated value to overwrite.
                    DateTime minVoteTime = allVotes.Min(voter => voter.Item2);

                    // Only overwrite an existing vote if the local time is greater than the current minimum vote time.
                    if (voteTime >= minVoteTime)
                    {
                        indexToWrite = allVotes.FindIndex(voter => voter.Item2.Equals(minVoteTime));
                    }
                }
            }

            if (indexToWrite == -1)
            {
                AddSuspector(localSilo, voteTime);
            }
            else
            {
                var newEntry = new Tuple<SiloAddress, DateTime>(localSilo, voteTime);
                SuspectTimes[indexToWrite] = newEntry;
            }
        }

        /// <summary>
        /// Adds a silo's vote to declare this silo unavailable.
        /// </summary>
        /// <param name="suspectingSilo">The silo casting the vote.</param>
        /// <param name="suspectingTime">The time at which the vote was cast.</param>
        public void AddSuspector(SiloAddress suspectingSilo, DateTime suspectingTime)
            => (SuspectTimes ??= new()).Add(Tuple.Create(suspectingSilo, suspectingTime));

        internal MembershipEntry Copy()
        {
            var copy = new MembershipEntry
            {
                SiloAddress = SiloAddress,
                Status = Status,
                SuspectTimes = SuspectTimes is null ? null : new(SuspectTimes),
                ProxyPort = ProxyPort,
                HostName = HostName,
                SiloName = SiloName,
                RoleName = RoleName,
                UpdateZone = UpdateZone,
                FaultZone = FaultZone,
                StartTime = StartTime,
                IAmAliveTime = IAmAliveTime,
                Metadata = Metadata
            };

            return copy;
        }

        internal MembershipEntry WithStatus(SiloStatus status)
        {
            var updated = this.Copy();
            updated.Status = status;
            return updated;
        }

        internal MembershipEntry WithIAmAliveTime(DateTime iAmAliveTime)
        {
            var updated = this.Copy();
            updated.IAmAliveTime = iAmAliveTime;
            return updated;
        }

        internal ImmutableList<Tuple<SiloAddress, DateTime>> GetFreshVotes(DateTime now, TimeSpan expiration)
        {
            if (this.SuspectTimes == null)
                return ImmutableList<Tuple<SiloAddress, DateTime>>.Empty;

            var result = ImmutableList.CreateBuilder<Tuple<SiloAddress, DateTime>>();

            // Find the latest time from the set of suspect times and the local time.
            // This prevents local clock skew from resulting in a different tally of fresh votes.
            var recencyWindowEndTime = Max(now, SuspectTimes);
            foreach (var voter in this.SuspectTimes)
            {
                // If now is smaller than otherVoterTime, than assume the otherVoterTime is fresh.
                // This could happen if clocks are not synchronized and the other voter clock is ahead of mine.
                var suspectTime = voter.Item2;
                if (recencyWindowEndTime.Subtract(suspectTime) < expiration)
                {
                    result.Add(voter);
                }
            }

            return result.ToImmutable();

            static DateTime Max(DateTime localTime, List<Tuple<SiloAddress, DateTime>> suspectTimes)
            {
                var maxValue = localTime;
                foreach (var entry in suspectTimes)
                {
                    var suspectTime = entry.Item2;
                    if (suspectTime > maxValue) maxValue = suspectTime;
                }

                return maxValue;
            }
        }

        /// <inheritdoc />
        public override string ToString() => $"SiloAddress={SiloAddress} SiloName={SiloName} Status={Status}";

        string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

        bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
            => destination.TryWrite($"SiloAddress={SiloAddress} SiloName={SiloName} Status={Status}", out charsWritten);

        /// <summary>
        /// Returns a detailed string representation of the membership entry.
        /// </summary>
        /// <returns>A detailed string representation of the membership entry.</returns>
        public string ToFullString()
        {
            var suspecters = SuspectTimes == null ? null : Utils.EnumerableToString(SuspectTimes.Select(tuple => tuple.Item1));
            var suspectTimes = SuspectTimes == null ? null : Utils.EnumerableToString(SuspectTimes.Select(tuple => LogFormatter.PrintDate(tuple.Item2)));

            return @$"[SiloAddress={SiloAddress} SiloName={SiloName} Status={Status} HostName={HostName} ProxyPort={ProxyPort} RoleName={RoleName} UpdateZone={UpdateZone} FaultZone={FaultZone} StartTime={LogFormatter.PrintDate(StartTime)} IAmAliveTime={LogFormatter.PrintDate(IAmAliveTime)} MetadataAvailable={Metadata is not null} MetadataCount={Metadata?.Count ?? 0}{(suspecters == null ? null : " Suspecters=")}{suspecters}{(suspectTimes == null ? null : " SuspectTimes=")}{suspectTimes}]";
        }
    }
}
