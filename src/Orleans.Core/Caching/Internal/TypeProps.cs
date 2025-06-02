using System;

namespace Orleans.Caching.Internal;

// https://source.dot.net/#System.Collections.Concurrent/System/Collections/Concurrent/ConcurrentDictionary.cs,2293
internal static class TypeProps<T>
{
    /// <summary>Whether T's type can be written atomically (i.e., with no danger of torn reads).</summary>
    internal static readonly bool IsWriteAtomic = IsWriteAtomicPrivate();

    private static bool IsWriteAtomicPrivate()
    {
        // Section 12.6.6 of ECMA CLI explains which types can be read and written atomically without
        // the risk of tearing. See https://www.ecma-international.org/publications/files/ECMA-ST/ECMA-335.pdf

        if (!typeof(T).IsValueType ||
            typeof(T) == typeof(nint) ||
            typeof(T) == typeof(nuint))
        {
            return true;
        }

        switch (Type.GetTypeCode(typeof(T)))
        {
            case TypeCode.Boolean:
            case TypeCode.Byte:
            case TypeCode.Char:
            case TypeCode.Int16:
            case TypeCode.Int32:
            case TypeCode.SByte:
            case TypeCode.Single:
            case TypeCode.UInt16:
            case TypeCode.UInt32:
                return true;

            case TypeCode.Double:
            case TypeCode.Int64:
            case TypeCode.UInt64:
                return nint.Size == 8;

            default:
                return false;
        }
    }
}
