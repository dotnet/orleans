using System;

namespace Orleans.Providers
{
#pragma warning disable 1574
#pragma warning restore 1574

    public static class ProviderConfigurationExtensions
    {
        public static int GetIntProperty(this IProviderConfiguration config, string key, int settingDefault)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }
            string s;
            return config.Properties.TryGetValue(key, out s) ? int.Parse(s) : settingDefault;
        }

        public static string GetProperty(this IProviderConfiguration config, string key, string settingDefault)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }
            string s;
            return config.Properties.TryGetValue(key, out s) ? s : settingDefault;
        }

        public static Guid GetGuidProperty(this IProviderConfiguration config, string key, Guid settingDefault)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }
            string s;
            return config.Properties.TryGetValue(key, out s) ? Guid.Parse(s) : settingDefault;
        }

        public static T GetEnumProperty<T>(this IProviderConfiguration config, string key, T settingDefault)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }
            string s;
            return config.Properties.TryGetValue(key, out s) ? (T)Enum.Parse(typeof(T),s) : settingDefault;
        }

        public static Type GetTypeProperty(this IProviderConfiguration config, string key, Type settingDefault)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }
            string s;
            return config.Properties.TryGetValue(key, out s) ? Type.GetType(s) : settingDefault;
        }

        public static bool GetBoolProperty(this IProviderConfiguration config, string key, bool settingDefault)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }
            string s;
            return config.Properties.TryGetValue(key, out s) ? bool.Parse(s) : settingDefault;
        }

        public static TimeSpan GetTimeSpanProperty(this IProviderConfiguration config, string key, TimeSpan settingDefault)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }
            string s;
            return config.Properties.TryGetValue(key, out s) ? TimeSpan.Parse(s) : settingDefault;
        }
    }
}
