using System;
using System.Threading.Tasks;

namespace Orleans.UnitTest.GrainInterfaces
{
    public class MyTypeWithAPrivateTypeField
    {
        private MyPrivateDependency _dependency;

        public MyTypeWithAPrivateTypeField()
        {
            _dependency = new MyPrivateDependency();
        }

    private class MyPrivateDependency
        {

        }
    }

    [Serializable]
    [GenerateSerializer]
    public class MyTypeWithAnInternalTypeField
    {
        [Id(0)]
        private MyInternalDependency _dependency;

        public MyTypeWithAnInternalTypeField()
        {
            _dependency = new MyInternalDependency();
        }
        internal class MyInternalDependency
        {

        }
    }

    // Verify that we do not try to generate a custom serializer for MyTypeWithAPrivateTypeField.
    // If we do, compilation will fail.
    public interface IPrivateReturnType : IGrainWithIntegerKey
    {
        Task<MyTypeWithAPrivateTypeField> Foo();
    }

    // Verify that we do generate a custom serializer for MyTypeWithAnInternalTypeField because it is visible within the assembly.
    public interface IInternalReturnType : IGrainWithIntegerKey
    {
        Task<MyTypeWithAnInternalTypeField> Foo();
    }
}
