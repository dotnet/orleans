namespace Orleans.Runtime
{
    /// <summary>
    /// The placement strategy used by system targets.
    /// </summary>
    [GenerateSerializer, Immutable, SuppressReferenceTracking]
    public sealed class SystemTargetPlacementStrategy : PlacementStrategy
    {
        public static SystemTargetPlacementStrategy Instance { get; } = new();

        public override bool IsUsingGrainDirectory => false;
    }
}
