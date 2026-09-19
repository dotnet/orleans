namespace Orleans.Runtime.ClusterServices;

/// <summary>
/// A strongly consistent, complete snapshot plus opaque compare-and-swap token.
/// Absence is represented by both fields being null; tokens never act as service fencing.
/// </summary>
internal sealed class ClusterServiceRegisterRead
{
    public ClusterServiceRegisterRead(RegisteredClusterServiceView? view, string? token)
    {
        if ((view is null) != (token is null) || token is { Length: 0 })
        {
            throw new ArgumentException("An absent register has neither a view nor a token; an existing register requires both.");
        }

        View = view;
        Token = token;
    }

    public RegisteredClusterServiceView? View { get; }
    public string? Token { get; }
}

/// <summary>
/// One configured service/authority register. Implementations must use atomic primary reads and conditional writes.
/// Register deletion/restoration is an administrative bootstrap, never an ordinary mapping change.
/// </summary>
internal interface IClusterServiceViewRegister
{
    ValueTask<ClusterServiceRegisterRead> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns the token from the successful conditional write, or null when its condition failed.
    /// </summary>
    ValueTask<string?> TryWriteAsync(RegisteredClusterServiceView view, string? expectedToken, CancellationToken cancellationToken);
}
