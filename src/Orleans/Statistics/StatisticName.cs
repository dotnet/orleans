using System;

namespace Orleans.Runtime
{
    internal class StatisticName
    {
        public string Name { get; private set; }

        public StatisticName(string name)
        {
            Name = name;
        }

        public StatisticName(StatisticNameFormat nameFormat, params object[] args)
        {
            Name = String.Format(nameFormat.Name, args);
        }

        public override string ToString()
        {
            return Name;
        }
    }
}