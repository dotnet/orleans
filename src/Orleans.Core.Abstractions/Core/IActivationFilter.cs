using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// A delegate used to intercept grain activation and deactivation.
    /// </summary>
    /// <param name="context">The activation context.</param>
    /// <returns>A <see cref="Task"/> which must be awaited before processing continues.</returns>
    public delegate Task ActivationFilterDelegate(IActivationContext context);

    /// <summary>
    /// Interface for grain activation and deactivation filters.
    /// </summary>
    public interface IActivationFilter
    {
        /// <summary>
        /// Invokes this filter.
        /// </summary>
        /// <param name="context">The activation context.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task Invoke(IActivationContext context);
    }

    /// <summary>
    /// Represents a grain activation or deactivation.
    /// </summary>
    public interface IActivationContext
    {
        /// <summary>
        /// Gets the grain context of the activation.
        /// </summary>
        IGrainContext GrainContext { get; }

        /// <summary>
        /// Gets the grain instance being activated or deactivated.
        /// </summary>
        object Grain { get; }

        /// <summary>
        /// Gets the identity of the grain.
        /// </summary>
        GrainId GrainId { get; }

        /// <summary>
        /// Gets which lifecycle operation is running.
        /// </summary>
        ActivationStage Stage { get; }

        /// <summary>
        /// Gets the reason for deactivation. Meaningful only when <see cref="Stage"/> is <see cref="ActivationStage.Deactivating"/>.
        /// </summary>
        DeactivationReason DeactivationReason { get; }

        /// <summary>
        /// Invokes the next filter in the pipeline, or the grain's
        /// <see cref="IGrainBase.OnActivateAsync"/> / <see cref="IGrainBase.OnDeactivateAsync"/>
        /// when no filters remain.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task Invoke();
    }

    /// <summary>
    /// Which lifecycle operation an <see cref="IActivationContext"/> represents.
    /// </summary>
    public enum ActivationStage
    {
        /// <summary>
        /// The grain is being activated.
        /// </summary>
        Activating,

        /// <summary>
        /// The grain is being deactivated.
        /// </summary>
        Deactivating,
    }
}
