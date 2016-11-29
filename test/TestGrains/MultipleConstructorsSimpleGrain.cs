using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class MultipleConstructorsSimpleGrain : SimpleGrain, ISimpleGrain
    {
        public const string MultipleConstructorsSimpleGrainPrefix = "UnitTests.Grains.MultipleConstructorsS";
        public const int ValueUsedByParameterlessConstructor = 42;

        public MultipleConstructorsSimpleGrain()
            : this(ValueUsedByParameterlessConstructor)
        {
            // orleans will use this constructor when DI is not configured
        }

        public MultipleConstructorsSimpleGrain(int initialValueofA)
        {
            base.A = initialValueofA;
        }
    }
}
