using System.Threading.Tasks;
using GrainInterfaces;
using Orleans;

namespace Grains
{
    public class CalculatorGrain : Grain, ICalculatorGrain
    {
        private readonly GrainObserverManager<ICalculatorObserver> observers = new GrainObserverManager<ICalculatorObserver>();
        private double current;

        public Task<double> Add(double value)
        {
            var result = this.current += value;
            this.observers.Notify(observer => observer.CalculationUpdated(result));
            return Task.FromResult(result);
        }

        public Task<double> Divide(double value)
        {
            var result = this.current /= value;
            this.observers.Notify(observer => observer.CalculationUpdated(result));
            return Task.FromResult(result);
        }

        public Task<double> Get()
        {
            return Task.FromResult(current);
        }

        public Task<double> Multiply(double value)
        {
            var result = current *= value;
            this.observers.Notify(observer => observer.CalculationUpdated(result));
            return Task.FromResult(result);
        }

        public Task<double> Set(double value)
        {
            var result = current = value;
            this.observers.Notify(observer => observer.CalculationUpdated(result));
            return Task.FromResult(result);
        }

        public Task<double> Subtract(double value)
        {
            var result = this.current -= value;
            this.observers.Notify(observer => observer.CalculationUpdated(result));
            return Task.FromResult(result);
        }

        public Task Subscribe(ICalculatorObserver observer)
        {
            observers.Subscribe(observer);
            return Task.FromResult(0);
        }
    }
}
