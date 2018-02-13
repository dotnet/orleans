// Code from https://github.com/aspnet/Options/blob/edc21af4166574ecd45d8f8dbd381dfec044f367/src/Microsoft.Extensions.Options/PostConfigureOptions.cs
// This will be removed and superseded Mirosoft.Extensions.Options v2.1.0.0 once it ships.

using System;
using Microsoft.Extensions.Options;

namespace Orleans.Configuration
{
    /// <summary>
    /// Implementation of IPostConfigureOptions.
    /// </summary>
    /// <typeparam name="TOptions"></typeparam>
    /// <typeparam name="TDep"></typeparam>
    internal class PostConfigureOptions<TOptions, TDep> : IPostConfigureOptions<TOptions>
        where TOptions : class
        where TDep : class
    {
        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="name">The name of the options.</param>
        /// <param name="dependency">A dependency.</param>
        /// <param name="action">The action to register.</param>
        public PostConfigureOptions(string name, TDep dependency, Action<TOptions, TDep> action)
        {
            Name = name;
            Action = action;
            Dependency = dependency;
        }

        /// <summary>
        /// The options name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The configuration action.
        /// </summary>
        public Action<TOptions, TDep> Action { get; }

        public TDep Dependency { get; }

        public virtual void PostConfigure(string name, TOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            // Null name is used to configure all named options.
            if (Name == null || name == Name)
            {
                Action?.Invoke(options, Dependency);
            }
        }

        public void PostConfigure(TOptions options) => PostConfigure(Options.DefaultName, options);
    }

    /// <summary>
    /// Implementation of IPostConfigureOptions.
    /// </summary>
    /// <typeparam name="TOptions"></typeparam>
    /// <typeparam name="TDep1"></typeparam>
    /// <typeparam name="TDep2"></typeparam>
    internal class PostConfigureOptions<TOptions, TDep1, TDep2> : IPostConfigureOptions<TOptions>
        where TOptions : class
        where TDep1 : class
        where TDep2 : class
    {
        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="name">The name of the options.</param>
        /// <param name="dependency">A dependency.</param>
        /// <param name="dependency2">A second dependency.</param>
        /// <param name="action">The action to register.</param>
        public PostConfigureOptions(string name, TDep1 dependency, TDep2 dependency2, Action<TOptions, TDep1, TDep2> action)
        {
            Name = name;
            Action = action;
            Dependency1 = dependency;
            Dependency2 = dependency2;
        }

        /// <summary>
        /// The options name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The configuration action.
        /// </summary>
        public Action<TOptions, TDep1, TDep2> Action { get; }

        public TDep1 Dependency1 { get; }

        public TDep2 Dependency2 { get; }

        public virtual void PostConfigure(string name, TOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            // Null name is used to configure all named options.
            if (Name == null || name == Name)
            {
                Action?.Invoke(options, Dependency1, Dependency2);
            }
        }

        public void PostConfigure(TOptions options) => PostConfigure(Options.DefaultName, options);
    }

    /// <summary>
    /// Implementation of IPostConfigureOptions.
    /// </summary>
    /// <typeparam name="TOptions"></typeparam>
    /// <typeparam name="TDep1"></typeparam>
    /// <typeparam name="TDep2"></typeparam>
    /// <typeparam name="TDep3"></typeparam>
    internal class PostConfigureOptions<TOptions, TDep1, TDep2, TDep3> : IPostConfigureOptions<TOptions>
        where TOptions : class
        where TDep1 : class
        where TDep2 : class
        where TDep3 : class
    {
        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="name">The name of the options.</param>
        /// <param name="dependency">A dependency.</param>
        /// <param name="dependency2">A second dependency.</param>
        /// <param name="dependency3">A third dependency.</param>
        /// <param name="action">The action to register.</param>
        public PostConfigureOptions(string name, TDep1 dependency, TDep2 dependency2, TDep3 dependency3, Action<TOptions, TDep1, TDep2, TDep3> action)
        {
            Name = name;
            Action = action;
            Dependency1 = dependency;
            Dependency2 = dependency2;
            Dependency3 = dependency3;
        }

        /// <summary>
        /// The options name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The configuration action.
        /// </summary>
        public Action<TOptions, TDep1, TDep2, TDep3> Action { get; }

        public TDep1 Dependency1 { get; }

        public TDep2 Dependency2 { get; }

        public TDep3 Dependency3 { get; }


        public virtual void PostConfigure(string name, TOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            // Null name is used to configure all named options.
            if (Name == null || name == Name)
            {
                Action?.Invoke(options, Dependency1, Dependency2, Dependency3);
            }
        }

        public void PostConfigure(TOptions options) => PostConfigure(Options.DefaultName, options);
    }

    /// <summary>
    /// Implementation of IPostConfigureOptions.
    /// </summary>
    /// <typeparam name="TOptions"></typeparam>
    /// <typeparam name="TDep1"></typeparam>
    /// <typeparam name="TDep2"></typeparam>
    /// <typeparam name="TDep3"></typeparam>
    /// <typeparam name="TDep4"></typeparam>
    internal class PostConfigureOptions<TOptions, TDep1, TDep2, TDep3, TDep4> : IPostConfigureOptions<TOptions>
        where TOptions : class
        where TDep1 : class
        where TDep2 : class
        where TDep3 : class
        where TDep4 : class
    {
        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="name">The name of the options.</param>
        /// <param name="dependency1">A dependency.</param>
        /// <param name="dependency2">A second dependency.</param>
        /// <param name="dependency3">A third dependency.</param>
        /// <param name="dependency4">A fourth dependency.</param>
        /// <param name="action">The action to register.</param>
        public PostConfigureOptions(string name, TDep1 dependency1, TDep2 dependency2, TDep3 dependency3, TDep4 dependency4, Action<TOptions, TDep1, TDep2, TDep3, TDep4> action)
        {
            Name = name;
            Action = action;
            Dependency1 = dependency1;
            Dependency2 = dependency2;
            Dependency3 = dependency3;
            Dependency4 = dependency4;
        }

        /// <summary>
        /// The options name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The configuration action.
        /// </summary>
        public Action<TOptions, TDep1, TDep2, TDep3, TDep4> Action { get; }

        public TDep1 Dependency1 { get; }

        public TDep2 Dependency2 { get; }

        public TDep3 Dependency3 { get; }

        public TDep4 Dependency4 { get; }


        public virtual void PostConfigure(string name, TOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            // Null name is used to configure all named options.
            if (Name == null || name == Name)
            {
                Action?.Invoke(options, Dependency1, Dependency2, Dependency3, Dependency4);
            }
        }

        public void PostConfigure(TOptions options) => PostConfigure(Options.DefaultName, options);
    }

    /// <summary>
    /// Implementation of IPostConfigureOptions.
    /// </summary>
    /// <typeparam name="TOptions"></typeparam>
    /// <typeparam name="TDep1"></typeparam>
    /// <typeparam name="TDep2"></typeparam>
    /// <typeparam name="TDep3"></typeparam>
    /// <typeparam name="TDep4"></typeparam>
    /// <typeparam name="TDep5"></typeparam>
    internal class PostConfigureOptions<TOptions, TDep1, TDep2, TDep3, TDep4, TDep5> : IPostConfigureOptions<TOptions>
        where TOptions : class
        where TDep1 : class
        where TDep2 : class
        where TDep3 : class
        where TDep4 : class
        where TDep5 : class
    {
        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="name">The name of the options.</param>
        /// <param name="dependency1">A dependency.</param>
        /// <param name="dependency2">A second dependency.</param>
        /// <param name="dependency3">A third dependency.</param>
        /// <param name="dependency4">A fourth dependency.</param>
        /// <param name="dependency5">A fifth dependency.</param>
        /// <param name="action">The action to register.</param>
        public PostConfigureOptions(string name, TDep1 dependency1, TDep2 dependency2, TDep3 dependency3, TDep4 dependency4, TDep5 dependency5, Action<TOptions, TDep1, TDep2, TDep3, TDep4, TDep5> action)
        {
            Name = name;
            Action = action;
            Dependency1 = dependency1;
            Dependency2 = dependency2;
            Dependency3 = dependency3;
            Dependency4 = dependency4;
            Dependency5 = dependency5;
        }

        /// <summary>
        /// The options name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The configuration action.
        /// </summary>
        public Action<TOptions, TDep1, TDep2, TDep3, TDep4, TDep5> Action { get; }

        public TDep1 Dependency1 { get; }

        public TDep2 Dependency2 { get; }

        public TDep3 Dependency3 { get; }

        public TDep4 Dependency4 { get; }

        public TDep5 Dependency5 { get; }


        public virtual void PostConfigure(string name, TOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            // Null name is used to configure all named options.
            if (Name == null || name == Name)
            {
                Action?.Invoke(options, Dependency1, Dependency2, Dependency3, Dependency4, Dependency5);
            }
        }

        public void PostConfigure(TOptions options) => PostConfigure(Options.DefaultName, options);
    }

}