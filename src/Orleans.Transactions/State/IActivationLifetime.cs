using System.Threading;
using Orleans.Internal;

namespace Orleans.Transactions.State
{
    internal interface IActivationLifetime
    {
        CancellationToken OnDeactivating { get; }

        /// <summary>
        /// Attempts to admit work into the activation's deactivation drain.
        /// Keep the result in a single <c>using</c> local and check
        /// <see cref="AdmissionGate.Admission.Entered"/> before starting work.
        /// </summary>
        AdmissionGate.Admission TryBlockDeactivation();
    }
}
