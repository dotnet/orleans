using System.Collections.Generic;

namespace Orleans.Serialization.Activators
{
    public class ListActivator<T>
    {
        public List<T> Create(int arg) => new List<T>(arg);
    }
}