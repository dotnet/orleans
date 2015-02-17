using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using System.Threading;
using UnitTests.GrainInterfaces;
using UnitTestGrains;

namespace GrainContextTestGrain
{
    public interface IGrainContextTestGrain : IGrain
    {
        Task<string> GetRuntimeInstanceId();
        Task<string> GetGrainContext_Immediate();
        Task<string> GetGrainContext_ContinueWithVoid();
        Task<string> GetGrainContext_DoubleContinueWith();
        Task<string> GetGrainContext_StartNew_Void();
        Task<string> GetGrainContext_ContinueWithException();
        Task<string> GetGrainContext_ContinueWithValueException();
        Task<string> GetGrainContext_ContinueWithValueAction();
        Task<string> GetGrainContext_ContinueWithValueActionException();
        Task<string> GetGrainContext_ContinueWithValueFunction();
    }
}