using System.Collections.Generic;
using Orleans;

namespace OrleansProviders.Streams.Memory;

/// <summary>
/// Maintains a lookup of stream provider names by their stable hashes.
/// </summary>
/// <remarks>
/// This map is expected to be populated at host build time and then only queried at runtime but not changed.
/// Therefore it is implemented with a regular non-concurrent collection to minimize perf impact.
/// </remarks>
public class MemoryStreamProviderHashLookup
{
    /// <summary>
    /// Maintains a map between stream provider names and their stable hashes.
    /// </summary>
    private readonly Dictionary<int, string> lookup = new();

    /// <summary>
    /// Computes the stable hash of the specified name and keeps it for future reference.
    /// Hash collisions are handled by last one wins rule.
    /// </summary>
    public int SetByName(string name)
    {
        var hash = (int)JenkinsHash.ComputeHash(name);

        this.lookup[hash] = name;

        return hash;
    }

    /// <summary>
    /// Attempts to get the name mapped from the specified hash.
    /// </summary>
    public string GetByHash(int hash) => this.lookup[hash];
}
