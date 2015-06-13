using Orleans;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UnitTests.GrainInterfaces
{
    public interface ISimpleServiceInterfaceNoParentMethods
    {
    }

    public interface ISimpleServiceInterfaceA
    {
        Task SetA(int a);

        Task IncrementA();

        Task<int> GetA();

        //Test: Uncommenting this function should throw compile error during code generation since non-async functions are not allowed up the hierarchy for a Grain interface.
        //int DecrementA();
    }

    public interface ISimpleServiceInterfaceB : ISimpleServiceInterfaceA
    {
        Task SetB(int b);

        Task IncrementB();

        Task<int> GetB();
    }

    public interface ISimpleGrainWithMultiInheritanceNoParentMehods : IGrain, ISimpleServiceInterfaceNoParentMethods 
    {
        //Test: To check for null pointer exception in the client generator if there are no methods.
        //Should compile without error/warnings.
    }

    public interface ISimpleGrainWithMultiInheritance : IGrainWithIntegerKey, ISimpleServiceInterfaceB
    {
        Task SetC(int c);
        Task IncrementC();
        Task<int> GetC();
    }
}
