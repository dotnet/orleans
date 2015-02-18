using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;


namespace UnitTestGrains
{
    /// <summary>
    /// A simple grain that allows to set two agruments and then multiply them.
    /// </summary>
    public class ErrorGrainWithAsyncMethods : ErrorGrain, IErrorGrainWithAsyncMethods
    {
        public Task IncrementAAsync_1()
        {
            Logger logger = GetLogger("ErrorGrainWithAsyncMethods");
            logger.Info("Started IncrementAAsync_1");

            return Task.Factory.StartNew(() =>
            {
                return Task.Factory.StartNew(() =>
                {
                    //Thread.Sleep(100); // just to delay resolution of the promise for testing purposes
                    logger.Info("Before base.IncrementA()");
                    return base.IncrementA();
                });
            }).Unwrap().Unwrap();
        }
    }
}
