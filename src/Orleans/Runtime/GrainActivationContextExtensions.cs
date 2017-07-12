using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Orleans.Runtime
{

    public static class GrainActivationContextExtensions
    {
        private const string BoundParametersKey = "BoundParameters";

        /// <summary>
        /// Select a constructor parameter to bind to by type.  Parameter of specified type will be 
        ///   selected in order in which they appear in the constructor.
        /// </summary>
        /// <param name="grainActivationContext">grain activation context of parameter</param>
        /// <param name="type">type of paramiter to select</param>
        /// <returns>Selected parameter</returns>
        public static ParameterInfo BindToConstructorParameter(this IGrainActivationContext grainActivationContext, Type type)
        {
            List<string> BoundParameters;
            object item;
            if (grainActivationContext.Items.TryGetValue(BoundParametersKey, out item))
            {
                BoundParameters = (List<string>)item;
            }
            else
            {
                BoundParameters = new List<string>(1);
                grainActivationContext.Items[BoundParametersKey] = BoundParameters;
            }
            ParameterInfo[] constructorParameters = grainActivationContext.ConstructorParameters;
            ParameterInfo parameter = constructorParameters
                    .Where(pi => type == pi.ParameterType)
                    .FirstOrDefault(pi => !BoundParameters.Contains(pi.Name)); // skip already selected parameters

            if (parameter == null) throw new InvalidOperationException("Could not find appropriate parameter in constructor");

            BoundParameters.Add(parameter.Name);
            return parameter;
        }

        /// <summary>
        /// Select a constructor parameter to bind to by type.  Parameter of specified type will be 
        ///   selected in order in which they appear in the constructor.
        /// </summary>
        /// <param name="grainActivationContext">grain activation context of parameter</param>
        /// <returns>Selected parameter</returns>
        public static ParameterInfo BindToConstructorParameter<TParmType>(this IGrainActivationContext grainActivationContext)
        {
            return grainActivationContext.BindToConstructorParameter(typeof(TParmType));
        }
    }
}
