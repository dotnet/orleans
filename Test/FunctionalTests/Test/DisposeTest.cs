using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DisposeTest
{
    internal class Disposable : IDisposable
    {
        private bool _isDisposed;

        public Disposable()
        {
            _isDisposed = false;
        }

        public void Dispose()
        {
            Console.WriteLine("Disposable.Dispose() called");
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            Console.WriteLine(String.Format("Disposable.Dispose({0}) called", disposing));
            if (_isDisposed)
                return;
            _isDisposed = true;
        }
    }

    internal class Finalized : IDisposable
    {
        private bool _isDisposed;

        public Finalized()
        {
            _isDisposed = false;
        }

        ~Finalized()
        {
            Console.WriteLine("~Finalized.Finalized called");
            Dispose(false);
        }

        public void Dispose()
        {
            Console.WriteLine("Finalized.Dispose() called");
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            Console.WriteLine(String.Format("Finalized.Dispose({0}) called", disposing));
            if (_isDisposed)
                return;
            _isDisposed = true;
        }
    }

    public static class Test
    {
        private static Tuple<WeakReference<Disposable>, WeakReference<Finalized>> MakeObjects()
        {
            Disposable d = new Disposable();
            Finalized f = new Finalized();
            return new Tuple<WeakReference<Disposable>, WeakReference<Finalized>>(new WeakReference<Disposable>(d), new WeakReference<Finalized>(f));
        }

        public static void Run()
        {
            Tuple<WeakReference<Disposable>, WeakReference<Finalized>> wd = MakeObjects();

            bool collected1 = false;
            bool collected2 = false;
            while (true)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Disposable unused1;
                collected1 = !wd.Item1.TryGetTarget(out unused1);
                Console.WriteLine("Object without finalizer was{0} collected.", collected1 ? "" : " not");

                Finalized unused2;
                collected2 = !wd.Item2.TryGetTarget(out unused2);
                Console.WriteLine("Object with finalizer was{0} collected.", collected2 ? "" : " not");

                Thread.Sleep(1000);
            }
        }
    }
}
