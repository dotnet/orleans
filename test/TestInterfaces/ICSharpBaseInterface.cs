using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UnitTests.Interfaces
{
    public interface ICSharpBaseInterface
    {
        Task<int> Echo(int x);
    }
}
