namespace Orleans.UnitTest.GrainInterfaces
{
    [Serializable]
    [GenerateSerializer]
    public class MyTypeWithAnInternalTypeField
    {
        [Id(0)]
        private readonly MyInternalDependency _dependency;

        public MyTypeWithAnInternalTypeField()
        {
            _dependency = new MyInternalDependency();
        }

        [GenerateSerializer]
        internal class MyInternalDependency
        {
        }
    }

    // Verify that we do generate a custom serializer for MyTypeWithAnInternalTypeField because it is visible within the assembly.
    public interface IInternalReturnType : IGrainWithIntegerKey
    {
        Task<MyTypeWithAnInternalTypeField> Foo();
    }
}
