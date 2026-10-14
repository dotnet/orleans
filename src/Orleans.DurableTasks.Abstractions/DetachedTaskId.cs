namespace Orleans.DurableTasks;

/// <summary>Derives root identifiers for detached tasks from the identifier of the execution which starts them.</summary>
/// <remarks>
/// A derived identifier is <c>{starterId}:{segment}</c>, where <c>{starterId}</c> is the starter's formatted
/// <see cref="TaskId"/> and <c>{segment}</c> contains no <c>:</c>. Distinct starters and segments therefore derive
/// distinct identifiers, a replay derives the same identifier, and observers outside the execution derive it from the
/// starter's identifier to poll or signal the detached task.
/// </remarks>
public static class DetachedTaskId
{
    private const char Separator = ':';

    /// <summary>Derives a detached task's root identifier from the current durable execution and <paramref name="segment"/>.</summary>
    /// <param name="segment">The detached task's name within the current execution, such as <c>release</c>.</param>
    /// <returns>The root identifier to pass to <c>DetachAsync</c>.</returns>
    /// <example>
    /// <code>await inventory.ReleaseAsync(orderId).DetachAsync(DetachedTaskId.For("release"));</code>
    /// </example>
    /// <exception cref="ArgumentNullException"><paramref name="segment"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="segment"/> is empty or contains <c>:</c>.</exception>
    /// <exception cref="InvalidOperationException">No durable execution is current.</exception>
    public static string For(string segment)
    {
        var context = DurableTaskContext.Ambient
            ?? throw new InvalidOperationException(
                "DetachedTaskId.For(segment) requires a durable execution context. Outside an execution, choose an explicit root identifier.");
        return For(context.Id, segment);
    }

    /// <summary>Derives a detached task's root identifier from its starter's identifier and <paramref name="segment"/>.</summary>
    /// <param name="starterId">The identifier of the execution which starts the detached task.</param>
    /// <param name="segment">The detached task's name within the starter's execution.</param>
    /// <returns>The root identifier which <see cref="For(string)"/> derives within the starter's execution.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="segment"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="starterId"/> is <see cref="TaskId.None"/>, or <paramref name="segment"/> is empty or contains <c>:</c>.
    /// </exception>
    public static string For(TaskId starterId, string segment)
    {
        if (starterId.IsDefault)
        {
            throw new ArgumentException("A detached task's starter requires an identifier.", nameof(starterId));
        }

        ArgumentException.ThrowIfNullOrEmpty(segment);
        if (segment.Contains(Separator, StringComparison.Ordinal))
        {
            throw new ArgumentException($"A detached task segment cannot contain '{Separator}'.", nameof(segment));
        }

        return $"{starterId}{Separator}{segment}";
    }
}
