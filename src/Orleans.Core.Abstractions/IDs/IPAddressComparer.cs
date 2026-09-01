using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Orleans.Runtime;

internal sealed class IPAddressComparer : IComparer<IPAddress>
{
    public static IPAddressComparer Instance { get; } = new();

    public int Compare(IPAddress? left, IPAddress? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        var leftFamily = left.AddressFamily;
        var rightFamily = right.AddressFamily;
        if (leftFamily != rightFamily)
        {
            return leftFamily < rightFamily ? -1 : 1;
        }

        if (leftFamily == AddressFamily.InterNetwork)
        {
#pragma warning disable CS0618 // Type or member is obsolete
            return left.Address.CompareTo(right.Address);
#pragma warning restore CS0618 // Type or member is obsolete
        }

        Span<byte> leftBytes = stackalloc byte[16];
        left.TryWriteBytes(leftBytes, out var length);
        Debug.Assert(length == 16);

        Span<byte> rightBytes = stackalloc byte[16];
        right.TryWriteBytes(rightBytes, out length);
        Debug.Assert(length == 16);

        var result = leftBytes.SequenceCompareTo(rightBytes);
        return result != 0 ? result : left.ScopeId.CompareTo(right.ScopeId);
    }
}
