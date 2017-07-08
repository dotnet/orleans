using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Orleans
{
    public static class ActivationServiceUtilities
    {
        private const string ActivationServiceNamesItemName = "ActivationServiceNamesItemName";
        private const string ConstructorParametersItemName = "ConstructorParameters";

        public static ParameterInfo BindToConstructorParameter(IGrainActivationContext grainActivationContext, Type activationServiceType)
        {
            List<string> configuredActivationServices;
            object item;
            if (grainActivationContext.Items.TryGetValue(ActivationServiceNamesItemName, out item))
            {
                configuredActivationServices = (List<string>)item;
            }
            else
            {
                configuredActivationServices = new List<string>(1);
                grainActivationContext.Items[ActivationServiceNamesItemName] = configuredActivationServices;
            }
            ParameterInfo[] constructorParameters = GetConstructorParameters(grainActivationContext);
            ParameterInfo parameter = constructorParameters
                    .Where(pi => activationServiceType == pi.ParameterType) // skip already configured ActivationServices
                    .FirstOrDefault(pi => !configuredActivationServices.Contains(pi.Name));

            if (parameter == null) throw new InvalidOperationException("Could not find appropriate parameter in constructor");

            configuredActivationServices.Add(parameter.Name);
            return parameter;
        }

        private static ParameterInfo[] GetConstructorParameters(IGrainActivationContext grainActivationContext)
        {
            object item;
            if (grainActivationContext.Items.TryGetValue(ConstructorParametersItemName, out item))
            {
                return (ParameterInfo[])item;
            }

            var constructor = grainActivationContext.GrainType.GetConstructors(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault();
            if (constructor == null) throw new InvalidOperationException("Could not find appropriate constructor");
            ParameterInfo[] constructorParameters = constructor.GetParameters();
            grainActivationContext.Items[ConstructorParametersItemName] = constructorParameters;
            return constructorParameters;
        }
    }
}
