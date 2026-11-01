namespace Orleans.Runtime;

/// <summary>
/// Answers how an activation admits a request relative to the calling code, such as its activation code or the request
/// which it belongs to, and opens exclusive sections, which hold requests back while code runs on the activation.
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
    /// Returns whether the calling code is this activation's activation code, such as its lifecycle's start or the grain's
    /// <c>OnActivateAsync</c>, which the activation runs before it invokes any request.
    /// </summary>
    /// <remarks>
    /// While the activation is being created or activated, it invokes no request, so code which runs on its scheduler then
    /// is activation code, or code which activation code started. Code which waits there for one of the activation's own
    /// requests waits until activation completes.
    /// </remarks>
    bool IsCurrentCodeActivating();

    /// <summary>
    /// Makes activations of this grain's type record, for the code which each request they invoke from now on runs, the
    /// request it belongs to, and, for the code which requests an exclusive section, that section.
    /// </summary>
    /// <remarks>
    /// Recording costs one <see cref="AsyncLocal{T}"/> write per request, and one per exclusive section, on activations of
    /// this type. Call it before the activation invokes requests whose code may call <see cref="IsBlockedByCurrentRequest"/>,
    /// such as while the activation activates.
    /// </remarks>
    void TrackCurrentRequest();

    /// <summary>
    /// Returns whether the activation holds <paramref name="request"/>, received with <paramref name="options"/>, until the
    /// calling code's own request completes or its own exclusive section closes.
    /// </summary>
    /// <param name="request">The local request, which this call evaluates without receiving it.</param>
    /// <param name="options">The options with which the activation would receive <paramref name="request"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the calling code holds this activation's open exclusive section and the section excludes
    /// <paramref name="request"/>, or when the calling code belongs to a running request of this activation and that request
    /// excludes <paramref name="request"/>. Otherwise <see langword="false"/>, including when another flow's section or another
    /// request holds <paramref name="request"/> back, since those end without the calling code, and for code which belongs
    /// to no running request and holds no section.
    /// </returns>
    bool IsBlockedByCurrentRequest(ILocalRequest request, LocalRequestOptions options);

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
