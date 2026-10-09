using System.Threading;

namespace Orleans.DurableMessaging;

internal static class DurableMessagingCancellation
{
    // A borrowed token needs no resource; distinct scopes still require a link owned by the caller.
    public static CancellationTokenSource? Combine(CancellationToken first, CancellationToken second, out CancellationToken token)
    {
        if (!first.CanBeCanceled || first == second)
        {
            token = second;
            return null;
        }
        if (!second.CanBeCanceled)
        {
            token = first;
            return null;
        }
        var source = CancellationTokenSource.CreateLinkedTokenSource(first, second);
        token = source.Token;
        return source;
    }

    public static CancellationTokenSource? Combine(
        CancellationToken first, CancellationToken second, CancellationToken third, out CancellationToken token)
    {
        if (!first.CanBeCanceled || first == second || first == third)
        {
            return Combine(second, third, out token);
        }
        if (!second.CanBeCanceled || second == third)
        {
            return Combine(first, third, out token);
        }
        if (!third.CanBeCanceled)
        {
            return Combine(first, second, out token);
        }
        var source = CancellationTokenSource.CreateLinkedTokenSource(first, second, third);
        token = source.Token;
        return source;
    }
}
