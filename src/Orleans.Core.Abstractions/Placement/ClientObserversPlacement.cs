namespace Orleans.Runtime
{
    [GenerateSerializer, Immutable, SuppressReferenceTracking]
    internal class ClientObserversPlacement : PlacementStrategy
    {
        public static ClientObserversPlacement Instance { get; } = new();
    }
}
