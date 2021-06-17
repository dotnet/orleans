using Microsoft.Extensions.Configuration;
using System.ComponentModel.DataAnnotations;

namespace OneBoxDeployment.Common
{
    /// <summary>
    /// Validates the given configuration object using <code>DataAnnotations attributes</code>./>
    /// </summary>
    public static class ValidationExtensions
    {
        /// <summary>
        /// Gets a valid object or throws a <see cref="ValidationException"/>.
        /// </summary>
        /// <typeparam name="T">The type of the object to get.</typeparam>
        /// <param name="configuration">the configuration object to rehydrate.</param>
        /// <returns>A valid object.</returns>
        public static T GetValid<T>(this IConfiguration configuration)
        {
            var obj = configuration.Get<T>();
            Validator.ValidateObject(obj, new ValidationContext(obj), true);

            return obj;
        }
    }
}
