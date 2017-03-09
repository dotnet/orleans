using System;

namespace Orleans.Runtime.Configuration
{
    [Serializable]
    internal class ConfigValue<T>
    {
        public T Value;
        public bool IsDefaultValue;

        public ConfigValue(T val, bool isDefaultValue)
        {
            Value = val;
            IsDefaultValue = isDefaultValue;
        }
    }
}