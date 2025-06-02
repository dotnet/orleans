namespace Orleans.Caching.Internal;

// Derived from BitFaster.Caching by Alex Peck
// https://github.com/bitfaster/BitFaster.Caching/blob/5b2d64a1afcc251787fbe231c6967a62820fc93c/BitFaster.Caching/Padding.cs
internal static class Padding
{
#if TARGET_ARM64 || TARGET_LOONGARCH64
    internal const int CACHE_LINE_SIZE = 128;
#else
    internal const int CACHE_LINE_SIZE = 64;
#endif
}
