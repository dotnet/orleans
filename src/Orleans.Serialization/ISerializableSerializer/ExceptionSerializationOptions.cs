using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;

namespace Orleans.Serialization
{
    /// <summary>
    /// Options for exception serialization.
    /// </summary>
    public class ExceptionSerializationOptions
    {
        internal Dictionary<Type, Func<Exception>> ExceptionFactories { get; } = new()
        {
            [typeof(Exception)] = static () => new Exception(),
            [typeof(SystemException)] = static () => new SystemException(),
            [typeof(ArgumentException)] = static () => new ArgumentException(),
            [typeof(ArgumentNullException)] = static () => new ArgumentNullException(),
            [typeof(ArgumentOutOfRangeException)] = static () => new ArgumentOutOfRangeException(),
            [typeof(ArithmeticException)] = static () => new ArithmeticException(),
            [typeof(DivideByZeroException)] = static () => new DivideByZeroException(),
            [typeof(OverflowException)] = static () => new OverflowException(),
            [typeof(FormatException)] = static () => new FormatException(),
            [typeof(IndexOutOfRangeException)] = static () => new IndexOutOfRangeException(),
            [typeof(InvalidCastException)] = static () => new InvalidCastException(),
            [typeof(InvalidOperationException)] = static () => new InvalidOperationException(),
            [typeof(NotImplementedException)] = static () => new NotImplementedException(),
            [typeof(NotSupportedException)] = static () => new NotSupportedException(),
            [typeof(NullReferenceException)] = static () => new NullReferenceException(),
            [typeof(ObjectDisposedException)] = static () => new ObjectDisposedException(null),
            [typeof(OperationCanceledException)] = static () => new OperationCanceledException(),
            [typeof(System.Threading.Tasks.TaskCanceledException)] = static () => new System.Threading.Tasks.TaskCanceledException(),
            [typeof(TimeoutException)] = static () => new TimeoutException(),
            [typeof(IOException)] = static () => new IOException(),
            [typeof(EndOfStreamException)] = static () => new EndOfStreamException(),
            [typeof(FileNotFoundException)] = static () => new FileNotFoundException(),
            [typeof(DirectoryNotFoundException)] = static () => new DirectoryNotFoundException(),
            [typeof(PathTooLongException)] = static () => new PathTooLongException(),
            [typeof(UnauthorizedAccessException)] = static () => new UnauthorizedAccessException(),
            [typeof(SerializationException)] = static () => new SerializationException(),
            [typeof(UnavailableExceptionFallbackException)] = static () => new UnavailableExceptionFallbackException()
        };

        /// <summary>
        /// Admits a reviewed exception type for reconstruction using the supplied factory.
        /// </summary>
        /// <typeparam name="TException">The concrete, closed exception type.</typeparam>
        /// <param name="factory">Creates an instance whose base exception properties will be restored.</param>
        /// <remarks>
        /// Registration authorizes initialization, construction, and restoration, including the type's
        /// virtual <see cref="Exception.Data"/> getter. Review those behaviors for the input trust boundary.
        /// The factory must return an instance of exactly <typeparamref name="TException"/>.
        /// <see cref="AggregateException"/> uses its dedicated serializer.
        /// </remarks>
        public void AddExceptionType<TException>(Func<TException> factory) where TException : Exception
        {
            if (factory is null) throw new ArgumentNullException(nameof(factory));
            var type = typeof(TException);
            if (type.IsAbstract || type.ContainsGenericParameters || type == typeof(AggregateException))
            {
                throw new ArgumentException("Register a concrete, closed exception type with its own factory.", nameof(factory));
            }

            ExceptionFactories[type] = () =>
            {
                var result = factory();
                if (result is null || result.GetType() != type)
                {
                    throw new SerializationException($"The exception factory for \"{type}\" must return that exact type.");
                }

                return result;
            };
        }

        /// <summary>
        /// Gets the collection of supported namespace prefixes for the exception serializer.
        /// Any exception type which has a namespace with one of these prefixes will be serialized using the exception serializer.
        /// </summary>
        public HashSet<string> SupportedNamespacePrefixes { get; } = new HashSet<string>(StringComparer.Ordinal) { "Microsoft", "System", "Azure" };

        /// <summary>
        /// Gets or sets the predicate used to select the exception codec for a type.
        /// </summary>
        /// <remarks>Use <see cref="AddExceptionType{TException}"/> to admit a custom type for reconstruction.</remarks>
        public Func<Type, bool> SupportedExceptionTypeFilter { get; set; } = _ => false;
    }
}
