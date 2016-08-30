using System.Collections.Generic;
using System.Reflection;

namespace Orleans.Runtime
{
    public interface IAssemblyCatalog
    {
        List<Assembly> GetAssemblies();
    }
}
