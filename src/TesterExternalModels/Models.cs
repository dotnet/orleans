using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TesterExternalModels
{
    [Serializable]
    public abstract class AbstractModel
    {
    }

    [Serializable]
    public class ConcreteModel : AbstractModel
    {        
    }

    [Serializable]
    public class EnumClass
    {
        public IEnumerable<MyEnum> EnumsList { get; set; }
    }

    [Serializable]
    public enum MyEnum
    {
        FirstOption
    }
}
