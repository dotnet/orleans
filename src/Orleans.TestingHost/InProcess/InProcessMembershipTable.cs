using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Runtime;
using System.Globalization;
using System.Threading.Tasks;
using Orleans.Messaging;

namespace Orleans.TestingHost.InProcess;

/// <summary>
/// An in-memory implementation of <see cref="IMembershipTable"/> for testing purposes.
/// </summary>
internal sealed class InProcessMembershipTable(string clusterId) : IMembershipTable, IGatewayListProvider
{
    private readonly Table _table = new();
    private readonly string _clusterId = clusterId;

    public TimeSpan MaxStaleness => TimeSpan.Zero;
    public bool IsUpdatable => true;

    public Task InitializeMembershipTable(bool tryInitTableVersion) => Task.CompletedTask;

    public Task DeleteMembershipTableEntries(string clusterId)
    {
        if (string.Equals(_clusterId, clusterId, StringComparison.Ordinal))
        {
            _table.Clear();
        }

        return Task.CompletedTask;
    }

    public Task<MembershipTableData> ReadRow(SiloAddress key) => Task.FromResult(_table.Read(key));

    public Task<MembershipTableData> ReadAll() => Task.FromResult(_table.ReadAll());

    public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion) => Task.FromResult(_table.Insert(entry, tableVersion));

    public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion) => Task.FromResult(_table.Update(entry, etag, tableVersion));

    public Task UpdateIAmAlive(MembershipEntry entry)
    {
        _table.UpdateIAmAlive(entry);
        return Task.CompletedTask;
    }

    public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
    {
        _table.CleanupDefunctSiloEntries(beforeDate);
        return Task.CompletedTask;
    }

    public Task InitializeGatewayListProvider() => Task.CompletedTask;

    public Task<IList<Uri>> GetGateways()
    {
        var table = _table.ReadAll();
        var result = table.Members
           .Where(x => x.Item1.Status == SiloStatus.Active && x.Item1.ProxyPort != 0)
           .Select(x =>
            {
                var entry = x.Item1;
                return SiloAddress.New(entry.SiloAddress.Endpoint.Address, entry.ProxyPort, entry.SiloAddress.Generation).ToGatewayUri();
            }).ToList();
        return Task.FromResult<IList<Uri>>(result);
    }

    public SiloStatus GetSiloStatus(SiloAddress address) => _table.GetSiloStatus(address);

    private sealed class Table
    {
        private readonly object _lock = new();
        private readonly Dictionary<SiloAddress, (MembershipEntry Entry, string ETag)> _table = [];
        private TableVersion _tableVersion;
        private long _lastETagCounter;

        public Table()
        {
            _tableVersion = new TableVersion(0, NewETag());
        }
        public SiloStatus GetSiloStatus(SiloAddress key)
        {
            lock (_lock)
            {
                return _table.TryGetValue(key, out var data) ? data.Entry.Status : SiloStatus.None;
            }
        }

        public MembershipTableData Read(SiloAddress key)
        {
            lock (_lock)
            {
                return _table.TryGetValue(key, out var data) ?
                    new MembershipTableData(Tuple.Create(data.Entry.Copy(), data.ETag), _tableVersion)
                    : new MembershipTableData(_tableVersion);
            }
        }

        public MembershipTableData ReadAll()
        {
            lock (_lock)
            {
                return new MembershipTableData(_table.Values.Select(data => Tuple.Create(data.Entry.Copy(), data.ETag)).ToList(), _tableVersion);
            }
        }

        public TableVersion ReadTableVersion() => _tableVersion;

        public bool Insert(MembershipEntry entry, TableVersion version)
        {
            lock (_lock)
            {
                if (_table.TryGetValue(entry.SiloAddress, out var data))
                {
                    return false;
                }

                if (!_tableVersion.VersionEtag.Equals(version.VersionEtag))
                {
                    return false;
                }

                _table[entry.SiloAddress] = (entry.Copy(), _lastETagCounter++.ToString(CultureInfo.InvariantCulture));
                _tableVersion = new TableVersion(version.Version, NewETag());
                return true;
            }
        }

        public bool Update(MembershipEntry entry, string etag, TableVersion version)
        {
            lock (_lock)
            {
                if (!_table.TryGetValue(entry.SiloAddress, out var data))
                {
                    return false;
                }

                if (!data.ETag.Equals(etag) || !_tableVersion.VersionEtag.Equals(version.VersionEtag))
                {
                    return false;
                }

                _table[entry.SiloAddress] = (entry.Copy(), _lastETagCounter++.ToString(CultureInfo.InvariantCulture));
                _tableVersion = new TableVersion(version.Version, NewETag());
                return true;
            }
        }

        public void UpdateIAmAlive(MembershipEntry entry)
        {
            lock (_lock)
            {
                if (!_table.TryGetValue(entry.SiloAddress, out var data))
                {
                    return;
                }

                data.Entry.IAmAliveTime = entry.IAmAliveTime;
                _table[entry.SiloAddress] = (data.Entry, NewETag());
            }
        }

        public void CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
        {
            lock (_lock)
            {
                var entries = _table.Values.ToList();
                foreach (var (entry, _) in entries)
                {
                    if (entry.Status == SiloStatus.Dead
                        && new DateTime(Math.Max(entry.IAmAliveTime.Ticks, entry.StartTime.Ticks), DateTimeKind.Utc) < beforeDate)
                    {
                        _table.Remove(entry.SiloAddress, out _);
                        continue;
                    }
                }
            }
        }

        internal void Clear()
        {
            lock (_lock)
            {
                _table.Clear();
            }
        }

        public override string ToString() => $"Table = {ReadAll()}, ETagCounter={_lastETagCounter}";

        private string NewETag() => _lastETagCounter++.ToString(CultureInfo.InvariantCulture);
    }
}
