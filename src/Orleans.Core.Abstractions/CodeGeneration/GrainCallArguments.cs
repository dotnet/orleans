using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace Orleans.CodeGeneration
{
    [Serializable]
    public partial struct GrainCallArguments : IGrainCallArguments
    {
        public object this[int index]
        {
            get => throw new ArgumentOutOfRangeException();
            set => throw new ArgumentOutOfRangeException();
        }

        public int Length => 0;

        int IReadOnlyCollection<object>.Count => Length;

        public Enumerator GetEnumerator() => new Enumerator();

        IEnumerator<object> IEnumerable<object>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void Visit<TContext>(IGrainCallArgumentVisitor<TContext> vistor, TContext context)
        {
        }

        public struct Enumerator : IEnumerator<object>
        {
            public object Current => null;

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                return false;
            }

            public void Reset()
            {
            }
        }

        public static GrainCallArguments Create() => new GrainCallArguments();
    }
}
