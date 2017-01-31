using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TestGrainInterfaces
{
   
    /// <summary>
    /// A grain that maintains a number of counters, indexed by a string key
    /// </summary>
    public interface ICountersGrain : Orleans.IGrainWithIntegerKey
    {
        /// <summary> Updates the counter for the given key by the given amount </summary>
        Task Add(string key, int amount, bool wait_till_persisted);

        /// <summary> Resets all counters to zero </summary>
        Task Reset(bool wait_till_persisted);

        /// <summary> Retrieves the counter for the given key </summary>
        Task<int> Get(string key);

        /// <summary> Retrieves the value of all counters </summary>
        Task<IReadOnlyDictionary<string, int>> GetAll();

    }
}
