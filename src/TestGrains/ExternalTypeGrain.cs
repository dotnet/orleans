using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;
using System.Collections.Specialized;

namespace UnitTests.Grains
{
    /// <summary>
    /// A simple grain that allows to set two arguments and then multiply them.
    /// </summary>
    public class ExternalTypeGrain : Grain, IExternalTypeGrain
    {
        public async Task GetAbstractModel(IEnumerable<NameObjectCollectionBase> list)
        {
            Console.WriteLine("GetAbstractModel: Success");
        }

        public async Task<EnumClass> GetEnumModel()
        {
            return new EnumClass() { EnumsList = new List<DateTimeKind>() { DateTimeKind.Local } };
        }
    }
}
