﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UnitTests.GrainInterfaces
{
    using Orleans;
    public interface IMethodInterceptionGrain : IGrainWithIntegerKey, IMethodFromAnotherInterface
    {
        Task<string> One();
        Task<string> Echo(string someArg);
        Task<string> NotIntercepted();
    }

    public interface IMethodFromAnotherInterface
    {
        Task<string> SayHello();
    }
}
