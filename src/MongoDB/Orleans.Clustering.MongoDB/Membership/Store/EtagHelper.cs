using System;

namespace Orleans.Providers.MongoDB.Membership.Store;

internal static class EtagHelper
{
    public static string CreateNew()
    {
        return Guid.NewGuid().ToString();
    }
}
