﻿using System.Threading.Tasks;
using Orleans;
using UnitTests.Interfaces;

namespace UnitTests.GrainInterfaces
{
    public interface IGeneratorTestDerivedFromCSharpInterfaceInExternalAssemblyGrain : IGrainWithGuidKey, ICSharpBaseInterface
    {
    }
}
