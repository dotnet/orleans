namespace Orleans.Placement.Repartitioning;

/// <summary>
/// Represents a rule that controls the degree of imbalance between the number of grain activations (that is considered tolerable), when any pair of silos are exchanging activations.
/// </summary>
public interface IImbalanceToleranceRule
{
    /// <summary>
    /// Checks if this rule is satisfied by <paramref name="imbalance"/>.
    /// </summary>
    /// <param name="imbalance">The imbalance between the exchanging silo pair that will be, if this method were to return <see langword="true"/></param>
    bool IsSatisfiedBy(uint imbalance);
}