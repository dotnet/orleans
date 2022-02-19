using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;

namespace Orleans.Serialization
{
    /// <summary>
    /// </summary>
    public interface ISerializerBuilderImplementation : ISerializerBuilder
    {
        ISerializerBuilderImplementation ConfigureServices(Action<IServiceCollection> configureDelegate);
        Dictionary<object, object>  Properties { get; }
    }
}