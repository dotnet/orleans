using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Concurrency;
using Orleans.Placement;


namespace UnitTests.GrainInterfaces
{
    public interface ILivenessTestGrain : IGrainWithIntegerKey
    {
        // duplicate to verify identity
        Task<long> GetKey();

        // separate label that can be set
        Task<string> GetLabel();

        Task SetLabel(string label);

        Task<string> GetRuntimeInstanceId();

        Task<string> GetUniqueId();

        Task<ILivenessTestGrain> GetGrainReference();

        Task<Tuple<string, string>> TestRequestContext();

        Task<IGrain[]> GetMultipleGrainInterfaces_Array();

        Task<List<IGrain>> GetMultipleGrainInterfaces_List();

        Task StartTimer();

        Task DoLongAction(TimeSpan timespan, string str);
    }
}
