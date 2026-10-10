namespace Orleans.Runtime;

internal enum RetirementDrainResult
{
    Succeeded,
    Failed,
    Canceled,
    Incomplete,
}
