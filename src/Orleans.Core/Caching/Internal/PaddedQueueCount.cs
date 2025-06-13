using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Orleans.Caching.Internal;

// Derived from BitFaster.Caching by Alex Peck
// https://github.com/bitfaster/BitFaster.Caching/blob/5b2d64a1afcc251787fbe231c6967a62820fc93c/BitFaster.Caching/Lru/PaddedQueueCount.cs
[DebuggerDisplay("Hot = {Hot}, Warm = {Warm}, Cold = {Cold}")]
[StructLayout(LayoutKind.Explicit, Size = 4 * Padding.CACHE_LINE_SIZE)] // padding before/between/after fields
internal struct PaddedQueueCount
{
    [FieldOffset(1 * Padding.CACHE_LINE_SIZE)] public int Hot;
    [FieldOffset(2 * Padding.CACHE_LINE_SIZE)] public int Warm;
    [FieldOffset(3 * Padding.CACHE_LINE_SIZE)] public int Cold;
}
