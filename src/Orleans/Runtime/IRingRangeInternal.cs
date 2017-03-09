namespace Orleans.Runtime
{
    internal interface IRingRangeInternal : IRingRange
    {
        long RangeSize();
        double RangePercentage();
        string ToFullString();
    }
}