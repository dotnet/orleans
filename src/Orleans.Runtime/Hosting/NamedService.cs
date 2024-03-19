using System;

namespace Orleans.Runtime.Hosting
{
    internal class NamedService<TService>(string name)
    {
        public string Name { get; } = name;
    }
}
