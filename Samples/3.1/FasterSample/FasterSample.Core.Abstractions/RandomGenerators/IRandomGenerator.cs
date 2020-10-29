using System.Diagnostics.CodeAnalysis;

namespace FasterSample.Core.RandomGenerators
{
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords")]
    public interface IRandomGenerator
    {
        public int Next(int minValue, int maxValue);

        public int Next(int maxValue);
    }
}