﻿using System;
using System.Threading;

namespace UnitTests.TestHelper
{
    [Serializable]
    public sealed class InterlockedFlag
    {
        private int _value;

        public InterlockedFlag()
        {
            _value = 0;
        }

        public bool IsSet { get { return _value != 0; } }

        public bool TrySet()
        {
            // attempt to set _value; if we're the first to attempt to do it, return true;
            return 0 == Interlocked.CompareExchange(ref _value, 1, 0);
        }

        public void ThrowNotInitializedIfSet()
        {
            if (IsSet)
                throw new InvalidOperationException("Attempt to access object that isn't initialized (or has been marked as dead).");
        }

        public void ThrowDisposedIfSet(Type type)
        {
            if (type == null)
                throw new ArgumentNullException("type");
            if (IsSet)
                throw new ObjectDisposedException(type.Name);
        }
    }
}
