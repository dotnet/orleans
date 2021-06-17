using System;
using System.Linq.Expressions;

namespace Orleans.Serialization.Activators
{
    public class DefaultActivator<T> : IActivator<T>
    {
        private static readonly Func<T> DefaultConstructorFunction;

        static DefaultActivator()
        {
            foreach (var ctor in typeof(T).GetConstructors())
            {
                if (ctor.GetParameters().Length != 0)
                {
                    continue;
                }

                var newExpression = Expression.New(ctor);
                DefaultConstructorFunction = Expression.Lambda<Func<T>>(newExpression).Compile();
                break;
            }
        }

        public T Create() => DefaultConstructorFunction != null ? DefaultConstructorFunction() : CreateUnformatted();

        private static T CreateUnformatted() => (T)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(T));
    }
}