using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;


namespace UnitTestGrains
{
    public class Outer1
    {
        public class Nested1
        {
        }

        public Nested1 nested;
        public Outer1(Nested1 data)
        {
        }
    }

    public interface ISimpleMainGrain1 : IGrain
    {
        Task Run();

        Task CheckNestedType(Outer1.Nested1 nested1);
    }

    internal class Outer2
    {
        internal class Nested2
        {
        }

        internal Nested2 nested;
        internal Outer2(Nested2 data)
        {
        }
    }

    internal interface ISimpleMainGrain2 : IGrain
    {
        Task Run();

        //Task CheckNestedType(Outer2.Nested2 nested2);
    }
}
