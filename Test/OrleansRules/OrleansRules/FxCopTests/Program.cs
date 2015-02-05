using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FxCopTestGrains;
using Orleans;

namespace FxCopTests
{
    class Program
    {/*
        static void Main(string[] args)
        {

        }

        public void DropPromise()
        {
            IGrain1 val = Grain1Factory.CreateGrain(null);
            val.SayHey("hey");
        }

        
        public void AssignAndDropPromise()
        {
            IGrain1 val = Grain1Factory.CreateGrain(null);
            AsyncValue<string> unresolved = val.SayHey("hey");
        }
        
        public void AssignPromiseAndWait()
        {
            IGrain1 val = Grain1Factory.CreateGrain(null);
            AsyncValue<string> promise = val.SayHey("hey");
            promise.Wait();
        }
        
        public void AssignPromiseAndIgnore()
        {
            IGrain1 val = Grain1Factory.CreateGrain(null);
            AsyncValue<string> promise = val.SayHey("hey");
            promise.Ignore();
        }
      */
        
        public void AssignPromiseAndContinueWith()
        {
            IGrain1 val = Grain1Factory.CreateGrain(null);
            AsyncValue<string> promise = val.SayHey("hey");
            promise.ContinueWith(() => Console.Out.WriteLine("Continuation, here!")).Wait();
        }
        /*
        public void ChainPromiseWait()
        {
            IGrain1 val = Grain1Factory.CreateGrain(null);
            val.SayHey("hey").Wait();
        }

        public void ChainPromiseIgnore()
        {
            IGrain1 val = Grain1Factory.CreateGrain(null);
            val.SayHey("hey").Ignore();
        }
        
        public void ChainPromiseContinueWith()
        {
            IGrain1 val = Grain1Factory.CreateGrain(null);
            val.SayHey("hey").ContinueWith(() => Console.Out.WriteLine("Continuation, here..."));
        }
                
        public AsyncValue<string> ReturnPromise()
        {
            IGrain1 val = Grain1Factory.CreateGrain(null);
            AsyncValue<string> promise = val.SayHey("Hey");
            return promise;
        }*/
    }
}
