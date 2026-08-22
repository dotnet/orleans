namespace Orleans.Runtime
{
    /// <summary>
    /// The placement strategy used by system targets.
    /// </summary>
    [GenerateSerializer, Immutable, SuppressReferenceTracking]
    public sealed class SystemTargetPlacementStrategy : PlacementStrategy
    {
        public static SystemTargetPlacementStrategy Instance { get; } = new();

        /// <inheritdoc />
        public override bool IsUsingGrainDirectory => false;

        /// <inheritdoc />
        public override bool IsGrain => false;
    }
}
