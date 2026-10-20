namespace Orleans.Runtime;

/// <summary>
/// Answers how an activation admits a request relative to the request which the calling code belongs to.
/// </summary>
/// <remarks>
/// The activation implements this component, which <c>IGrainContext.GetComponent</c> returns. Members
/// are called on the activation's scheduler. Admission follows the activation's own rules, as for any request: grain
/// reentrancy, <see cref="Concurrency.AlwaysInterleaveAttribute"/> and <see cref="Concurrency.ReentrantAttribute"/>,
/// <see cref="Concurrency.MayInterleaveAttribute"/> predicates, read-only requests, and call-chain reentrancy.
/// </remarks>
internal interface IGrainRequestAdmission
{
    /// <summary>
    /// Makes activations of this grain's type record, for the code which each request they invoke from now on runs, the
    /// request it belongs to.
    /// </summary>
    /// <remarks>
    /// Recording costs one <see cref="AsyncLocal{T}"/> write per request on activations of this type. Call it before the
    /// activation invokes requests whose code may call <see cref="IsBlockedByCurrentRequest"/>, such as while the
    /// activation activates.
    /// </remarks>
    void TrackCurrentRequest();

    /// <summary>
    /// Returns whether the activation holds <paramref name="request"/>, received with <paramref name="options"/>, until the
    /// running request which the calling code belongs to completes.
    /// </summary>
    /// <param name="request">The local request, which this call evaluates without receiving it.</param>
    /// <param name="options">The options with which the activation would receive <paramref name="request"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the calling code belongs to a running request of this activation and that request
    /// excludes <paramref name="request"/>; otherwise <see langword="false"/>, including for code which belongs to no running
    /// request, such as code which continues after its request completed.
    /// </returns>
    bool IsBlockedByCurrentRequest(ILocalRequest request, LocalRequestOptions options);
}
