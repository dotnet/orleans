using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.StorageClient;
using Orleans;

namespace Orleans.UnitTest.Unsigned.GrainInterfaces
{
    public class SubDictionary : Dictionary<int, ulong>
    {
    }

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
    public class MyTypeWithAnInternalTypeField
    {
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
    public interface IPrivateReturnType : IGrain
    {
        Task<MyTypeWithAPrivateTypeField> Foo();
    }

    // Verify that we do not try to generate a custom serializer for SubDictionary because Dictionary contains fileds of private types.
    // If we do, compilation will fail. 
    public interface ISubDictionaryReturnType : IGrain
    {
        Task<SubDictionary> Foo();
    }

    // Verify that we do generate a custom serializer for MyTypeWithAnInternalTypeField because it is visible within the assembly.
    public interface IInternalReturnType : IGrain
    {
        Task<MyTypeWithAnInternalTypeField> Foo();
    }
}
