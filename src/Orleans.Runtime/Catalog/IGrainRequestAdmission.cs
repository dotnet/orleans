namespace Orleans.Runtime;

/// <summary>
/// Opens exclusive sections, which hold an activation's requests back while code runs on the activation.
/// </summary>
/// <remarks>
/// The activation implements this component, which <c>IGrainContext.GetComponent</c> returns. Members
/// are called on the activation's scheduler. Outside exclusive sections, admission follows the activation's own rules, as for
/// any request: grain reentrancy, <see cref="Concurrency.AlwaysInterleaveAttribute"/> and
/// <see cref="Concurrency.ReentrantAttribute"/>, <see cref="Concurrency.MayInterleaveAttribute"/> predicates, read-only
/// requests, and call-chain reentrancy. <see cref="EnterExclusiveSectionAsync"/> describes admission while a section is open.
/// </remarks>
internal interface IGrainRequestAdmission
{
    /// <summary>
    /// Opens an exclusive section for code which runs on the activation's scheduler, such as a transaction. While the section
    /// is open, the activation starts requests which always interleave and requests on the section holder's call chain; all
    /// other requests wait for the section to close.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait for the open section to close.</param>
    /// <returns>
    /// The section, once no other exclusive section is open. Disposing it closes it once. Sections open one at a time, in the
    /// order in which they're requested.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The holder's call chain is the calling code's <see cref="RequestContext.ReentrancyId"/>, which this method reads
    /// synchronously, before it returns. A section which waits keeps the call chain of the code which requested it and
    /// carries it when it opens. A callback on the holder's call chain, which the holder allowed with
    /// <see cref="RequestContext.AllowCallChainReentrancy"/>, therefore runs inside the section instead of deadlocking.
    /// A holder whose reentrancy ID is <see cref="Guid.Empty"/> has no call chain, so while its section is open the activation
    /// starts only requests which always interleave.
    /// </para>
    /// <para>
    /// A section doesn't wait for requests which already run, so a caller which awaits the section from a grain method doesn't
    /// wait for itself. On a non-reentrant grain, a grain method's section therefore holds back every other request except
    /// those which always interleave and those on its call chain. On a reentrant grain, and for callers which don't run in a
    /// request, requests which were already running when the section opened can resume alongside it.
    /// </para>
    /// <para>
    /// An open section counts as activity, so idle collection keeps the activation while it's open. A canceled wait throws
    /// <see cref="OperationCanceledException"/> and gives up its place in the order. Sections which wait when the activation
    /// finishes deactivating fault with <see cref="InvalidOperationException"/>.
    /// </para>
    /// </remarks>
    ValueTask<IDisposable> EnterExclusiveSectionAsync(CancellationToken cancellationToken);
}
