using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Coordinates transactional operations across fault-injection test grains.
    /// </summary>
    public interface IFaultInjectionTransactionCoordinatorGrain : IGrainWithGuidKey
    {
        /// <summary>
        /// Sets the value of each grain in a new transaction.
        /// </summary>
        /// <param name="grains">The grains to update.</param>
        /// <param name="numberToAdd">The value to set on each grain.</param>
        /// <returns>A task representing the operation.</returns>
        [Transaction(TransactionOption.Create)]
        Task MultiGrainSet(List<IFaultInjectionTransactionTestGrain> grains, int numberToAdd);

        /// <summary>
        /// Adds a value to each grain in a new transaction and configures fault injection.
        /// </summary>
        /// <param name="grains">The grains to update.</param>
        /// <param name="numberToAdd">The value to add to each grain.</param>
        /// <param name="faultInjection">The fault to inject, or <see langword="null"/> to perform the operation without a controlled fault.</param>
        /// <returns>A task representing the operation.</returns>
        [Transaction(TransactionOption.Create)]
        Task MultiGrainAddAndFaultInjection(List<IFaultInjectionTransactionTestGrain> grains, int numberToAdd, 
            FaultInjectionControl? faultInjection = null);
    }

    /// <summary>
    /// Coordinates transactional operations across fault-injection test grains.
    /// </summary>
    public class FaultInjectionTransactionCoordinatorGrain : Grain, IFaultInjectionTransactionCoordinatorGrain
    {
        /// <inheritdoc />
        public Task MultiGrainSet(List<IFaultInjectionTransactionTestGrain> grains, int newValue)
        {
            return Task.WhenAll(grains.Select(g => g.Set(newValue)));
        }

        /// <inheritdoc />
        public Task MultiGrainAddAndFaultInjection(List<IFaultInjectionTransactionTestGrain> grains, int numberToAdd,
            FaultInjectionControl? faultInjection = null)
        {
            return Task.WhenAll(grains.Select(g => g.Add(numberToAdd, faultInjection)));
        }
    }
}
