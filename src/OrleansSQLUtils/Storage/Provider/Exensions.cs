using System;
using Orleans.Providers;

namespace Orleans.SqlUtils.StorageProvider
{
    internal static class Exensions
    {
        internal static string GetProperty(this IProviderConfiguration config, string propertyName)
        {
            string value = null;
            config.Properties.TryGetValue(propertyName, out value);
            return value;
        }

        internal static bool GetPropertyBool(this IProviderConfiguration config, string propertyName, bool def)
        {
            string strval = null;
            bool value;
            config.Properties.TryGetValue(propertyName, out strval);
            if (string.IsNullOrEmpty(strval) || !bool.TryParse(strval, out value))
                value = def;
            return value;
        }
    }
}