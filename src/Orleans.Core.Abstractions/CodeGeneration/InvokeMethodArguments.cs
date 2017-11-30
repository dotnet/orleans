using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace Orleans.CodeGeneration
{
    [Serializable]
    public struct InvokeMethodArguments : IList<object>, IEquatable<InvokeMethodArguments>
    {
        private object _argument;
        private object[] _arguments;

        internal object AsArgument => _argument;

        internal object[] AsArguments => _arguments;

        public static readonly InvokeMethodArguments Empty = new InvokeMethodArguments { _argument = NoArgumentMarks.Value };

        public static InvokeMethodArguments FromArgument(object argument)
        {
            return new InvokeMethodArguments
            {
                _argument = argument,
                _arguments = null
            };
        }

        public static InvokeMethodArguments FromArguments(params object[] arguments)
        {
            if(arguments == null) throw new ArgumentNullException(nameof(arguments));
            if (arguments.Length == 0 || arguments.Length == 1) throw new ArgumentException(nameof(arguments));

            return new InvokeMethodArguments
            {
                _argument = null,
                _arguments = arguments
            };
        }

        public bool IsEmpty => _argument == NoArgumentMarks.Value;

        public int Length
        {
            get
            {
                if (IsEmpty) return 0;
                if (_arguments == null) return 1;
                return _arguments.Length;
            }
        }

        int ICollection<object>.Count => Length;

        bool ICollection<object>.IsReadOnly => false;

        public object this[int index]
        {
            get
            {
                if (index < 0 || IsEmpty) throw new ArgumentOutOfRangeException(nameof(index));
                if (_arguments == null)
                {
                    if (index > 0) throw new ArgumentOutOfRangeException(nameof(index));
                    else return _argument;
                }

                return _arguments[index];
            }

            set
            {
                if (index < 0 || IsEmpty) throw new ArgumentOutOfRangeException(nameof(index));
                else if (_arguments == null)
                {
                    if (index > 0) throw new ArgumentOutOfRangeException(nameof(index));
                    else _argument = value;
                }
                else _arguments[index] = value;
            }
        }

        Enumerator GetEnumerator()
        {
            return new Enumerator(this);
        }

        IEnumerator<object> IEnumerable<object>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override bool Equals(object obj)
        {
            return obj is InvokeMethodArguments && Equals((InvokeMethodArguments)obj);
        }

        public bool Equals(InvokeMethodArguments other)
        {
            return EqualityComparer<object>.Default.Equals(_argument, other._argument) &&
                   EqualityComparer<object[]>.Default.Equals(_arguments, other._arguments);
        }

        public override int GetHashCode()
        {
            var hashCode = 617182527;
            hashCode = hashCode * -1521134295 + base.GetHashCode();
            hashCode = hashCode * -1521134295 + EqualityComparer<object>.Default.GetHashCode(_argument);
            hashCode = hashCode * -1521134295 + EqualityComparer<object[]>.Default.GetHashCode(_arguments);
            return hashCode;
        }

        int IList<object>.IndexOf(object item)
        {
            int index = -1;
            for (int i = 0; i < Length; i++)
            {
                if (this[i] == item)
                {
                    index = i;
                    break;
                }
            }

            return index;
        }

        void IList<object>.Insert(int index, object item) => throw new NotSupportedException();

        void IList<object>.RemoveAt(int index) => throw new NotSupportedException();

        void ICollection<object>.Add(object item) => throw new NotSupportedException();

        void ICollection<object>.Clear() => throw new NotSupportedException();

        bool ICollection<object>.Contains(object item)
        {
            for (int i = 0; i < Length; i++)
                if (this[i] == item) return true;

            return false;
        }

        void ICollection<object>.CopyTo(object[] array, int arrayIndex)
        {
            for (int i = 0; i < Length; i++)
                array[arrayIndex++] = this[i];
        }

        bool ICollection<object>.Remove(object item) => throw new NotSupportedException();

        public struct Enumerator : IEnumerator<object>
        {
            private InvokeMethodArguments _arguments;

            private int _index;

            internal Enumerator(InvokeMethodArguments arguments)
            {
                _arguments = arguments;
                _index = -1;
            }

            public object Current => _arguments[_index];

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                var next = _index + 1;
                if (next < _arguments.Length)
                {
                    _index = next;
                    return true;
                }

                return false;
            }

            public void Reset()
            {
                _index = -1;
            }
        }

        [Serializable]
        private sealed class NoArgumentMarks
        {
            public static readonly NoArgumentMarks Value = new NoArgumentMarks();
        }

        public static bool operator ==(InvokeMethodArguments arguments1, InvokeMethodArguments arguments2)
        {
            return arguments1.Equals(arguments2);
        }

        public static bool operator !=(InvokeMethodArguments arguments1, InvokeMethodArguments arguments2)
        {
            return !(arguments1 == arguments2);
        }
    }
}
