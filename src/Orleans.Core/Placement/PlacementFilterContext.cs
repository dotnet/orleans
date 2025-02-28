using Orleans.Runtime;

#nullable enable
namespace Orleans.Placement;

public readonly record struct PlacementFilterContext(GrainType GrainType, GrainInterfaceType InterfaceType, ushort InterfaceVersion);