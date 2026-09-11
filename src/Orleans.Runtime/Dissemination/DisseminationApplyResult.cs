namespace Orleans.Runtime.Dissemination;

internal enum DisseminationApplyResult
{
    Applied,
    Duplicate,
    Obsolete,
    Rejected,
}
