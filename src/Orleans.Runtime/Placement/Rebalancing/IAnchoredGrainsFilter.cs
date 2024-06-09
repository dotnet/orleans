namespace Orleans.Runtime.Placement.Rebalancing;

internal interface IAnchoredGrainsFilter
{
    void Add(GrainId id);
    bool Contains(GrainId id);
    void Reset();
}