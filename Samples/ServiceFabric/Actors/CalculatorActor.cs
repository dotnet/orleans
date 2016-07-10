using System.Threading.Tasks;
using GrainInterfaces;
using Orleans;

namespace Actors
{
    public class CalculatorActor : Grain, ICalculatorActor
    {
        private double current;

        public Task<double> Add(double value)
        {
            return Task.FromResult(current += value);
        }

        public Task<double> Divide(double value)
        {
            return Task.FromResult(current /= value);
        }

        public Task<double> Get()
        {
            return Task.FromResult(current);
        }

        public Task<double> Multiply(double value)
        {
            return Task.FromResult(current *= value);
        }

        public Task<double> Set(double value)
        {
            return Task.FromResult(current = value);
        }

        public Task<double> Subtract(double value)
        {
            return Task.FromResult(current -= value);
        }
    }
}
