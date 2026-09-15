using System.Collections.Immutable;
using Orleans.Concurrency;

namespace UnitTests.GrainInterfaces;

public interface IReadOnlySchedulingGrain : IGrainWithStringKey
{
    [ReadOnly]
    Task<string> ReadOnlyEcho(string value);

    Task<string> WritableEcho(string value);

    [AlwaysInterleave]
    Task<string> AlwaysInterleaveEcho(string value);

    [ReadOnly]
    [AlwaysInterleave]
    Task<string> ReadOnlyAlwaysInterleaveEcho(string value);

    [ReadOnly]
    Task BlockReadOnly(string operationId);

    Task BlockWritable(string operationId);

    [ReadOnly]
    [AlwaysInterleave]
    Task BlockReadOnlyAlwaysInterleave(string operationId);

    [AlwaysInterleave]
    Task WaitForEntry(string operationId);

    [AlwaysInterleave]
    Task Release(string operationId);

    [AlwaysInterleave]
    Task<ImmutableArray<string>> GetEvents();

    [AlwaysInterleave]
    Task Reset();

    [AlwaysInterleave]
    Task Checkpoint(string checkpointId);
}

public interface IReadOnlyInheritedInterfaceBaseGrain : IGrainWithStringKey
{
    [ReadOnly]
    Task<string> InheritedReadOnly();
}

public interface IReadOnlyInheritedInterfaceDerivedGrain : IReadOnlyInheritedInterfaceBaseGrain
{
}

public interface IReadOnlyRedeclaredBaseReadOnlyGrain : IGrainWithStringKey
{
    [ReadOnly]
    Task<string> Redeclared();
}

public interface IReadOnlyRedeclaredDerivedWritableGrain : IReadOnlyRedeclaredBaseReadOnlyGrain
{
    new Task<string> Redeclared();
}

public interface IReadOnlyRedeclaredBaseWritableGrain : IGrainWithStringKey
{
    Task<string> Redeclared();
}

public interface IReadOnlyRedeclaredDerivedReadOnlyGrain : IReadOnlyRedeclaredBaseWritableGrain
{
    [ReadOnly]
    new Task<string> Redeclared();
}

public interface IReadOnlyInheritedImplementationGrain : IGrainWithStringKey
{
    [ReadOnly]
    Task<string> InheritedImplementation();
}

public interface IImplementationOnlyReadOnlySchedulingGrain : IGrainWithStringKey
{
    Task<string> ImplementationOnly();
}

public interface IReadOnlyOverloadSchedulingGrain : IGrainWithStringKey
{
    [ReadOnly]
    Task<string> Overloaded(int value);

    Task<string> Overloaded(string value);
}

public interface IReadOnlyGenericMethodSchedulingGrain : IGrainWithStringKey
{
    [ReadOnly]
    Task<T> GenericReadOnly<T>(T value);
}

public interface IReadOnlyGenericSchedulingGrain<T> : IGrainWithStringKey
{
    [ReadOnly]
    Task<T> GenericReadOnly(T value);
}

public interface IReadOnlyPolicySchedulingGrain : IGrainWithStringKey
{
    [ReadOnly]
    Task BlockReadOnly(string operationId);

    Task BlockWritable(string operationId);

    [AlwaysInterleave]
    Task WaitForEntry(string operationId);

    [AlwaysInterleave]
    Task Release(string operationId);

    [AlwaysInterleave]
    Task<ImmutableArray<string>> GetEvents();

    [AlwaysInterleave]
    Task Checkpoint(string checkpointId);
}

public interface IReadOnlyReentrantSchedulingGrain : IReadOnlyPolicySchedulingGrain
{
}

public interface IReadOnlyMayInterleaveTrueSchedulingGrain : IReadOnlyPolicySchedulingGrain
{
}

public interface IReadOnlyMayInterleaveFalseSchedulingGrain : IReadOnlyPolicySchedulingGrain
{
}

public interface IReadOnlyMayInterleaveReadSchedulingGrain : IReadOnlyPolicySchedulingGrain
{
}
