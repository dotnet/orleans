namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Defines the integer state used by transaction test grains.
    /// </summary>
    public interface ITestState
    {
        /// <summary>
        /// Gets or sets the current test value.
        /// </summary>
        int state { get; set; }
    }
}
