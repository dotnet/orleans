using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Limits Manager
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class LimitManager
    {
        [Id(1)]
        private readonly Dictionary<string, LimitValue> limitValues;

        public LimitManager()
        {
            limitValues = new Dictionary<string, LimitValue>();
        }

        public LimitManager(LimitManager other)
        {
            limitValues = new Dictionary<string, LimitValue>(other.limitValues);
        }

        public void AddLimitValue(string name, LimitValue @value)
        {
            limitValues.Add(name, @value);
        }

        public LimitValue GetLimit(string name)
        {
            return GetLimit(name, 0, 0);
        }

        public LimitValue GetLimit(string name, int defaultSoftLimit)
        {
            return GetLimit(name, defaultSoftLimit, 0);
        }

        public LimitValue GetLimit(string name, int defaultSoftLimit, int defaultHardLimit)
        {
            LimitValue limit;
            if (limitValues.TryGetValue(name, out limit))
                return limit;

            return new LimitValue { Name = name, SoftLimitThreshold = defaultSoftLimit, HardLimitThreshold = defaultHardLimit };
        }

        public static LimitValue GetDefaultLimit(string name)
        {
            return new LimitValue { Name = name, SoftLimitThreshold = 0, HardLimitThreshold = 0 };
        }

        public override string ToString()
        {
            if (limitValues.Count > 0)
            {
                var sb = new StringBuilder();
                sb.Append("   Limits Values: ").AppendLine();
                foreach (var limit in limitValues.Values)
                {
                    sb.AppendFormat("       {0}", limit).AppendLine();
                }
                return sb.ToString();
            }
            return String.Empty;
        }
    }
}
