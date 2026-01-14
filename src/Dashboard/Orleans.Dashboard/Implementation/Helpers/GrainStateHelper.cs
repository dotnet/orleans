using Orleans.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Orleans.Dashboard.Implementation.Helpers;

internal static class GrainStateHelper
{
    public static (object, string) GetGrainId(string id, Type implementationType)
    {
        object grainId = null;
        string keyExtension = "";
        var splitedGrainId = id.Split(",");

        try
        {
            if (implementationType.IsAssignableTo(typeof(IGrainWithGuidCompoundKey)))
            {
                if (splitedGrainId.Length != 2)
                    throw new InvalidOperationException("Inform grain id in format `{ id},{additionalKey}`");

                grainId = Guid.Parse(splitedGrainId.First());
                keyExtension = splitedGrainId.Last();
            }
            else if (implementationType.IsAssignableTo(typeof(IGrainWithIntegerCompoundKey)))
            {
                if (splitedGrainId.Length != 2)
                    throw new InvalidOperationException("Inform grain id in format {id},{additionalKey}");

                grainId = Convert.ToInt64(splitedGrainId.First());
                keyExtension = splitedGrainId.Last();
            }
            else if (implementationType.IsAssignableTo(typeof(IGrainWithIntegerKey)))
            {
                grainId = Convert.ToInt64(id);
            }
            else if (implementationType.IsAssignableTo(typeof(IGrainWithGuidKey)))
            {
                grainId = Guid.Parse(id);
            }
            else if (implementationType.IsAssignableTo(typeof(IGrainWithStringKey)))
            {
                grainId = id;
            }
        }
        catch (Exception ex)
        {
            throw new Exception("Error when trying to convert grain Id", ex);
        }

        return (grainId, keyExtension);
    }

    public static IEnumerable<Type> GetPropertiesAndFieldsForGrainState(Type implementationType)
    {
        var impProperties = implementationType.GetProperties(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);

        var impFields = implementationType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);

        var filterProps = impProperties
                            .Where(w => w.PropertyType.IsAssignableTo(typeof(IStorage)))
                            .Select(s => s.PropertyType.GetGenericArguments().First());

        var filterFields = impFields
                            .Where(w => w.FieldType.IsAssignableTo(typeof(IStorage)))
                            .Select(s => s.FieldType.GetGenericArguments().First());

        return filterProps.Union(filterFields);
    }

    public static MethodInfo GenerateGetGrainMethod(IGrainFactory grainFactory, object grainId, string keyExtension)
    {
        if (string.IsNullOrWhiteSpace(keyExtension))
        {
            return grainFactory.GetType().GetMethods()
                            .First(w => w.Name == "GetGrain"
                                  && w.GetParameters().Count() == 2
                                  && w.GetParameters()[0].ParameterType == typeof(Type)
                                  && w.GetParameters()[1].ParameterType == grainId.GetType());
        }
        else
        {
            return grainFactory.GetType().GetMethods()
                            .First(w => w.Name == "GetGrain"
                                  && w.GetParameters().Count() == 3
                                  && w.GetParameters()[0].ParameterType == typeof(Type)
                                  && w.GetParameters()[1].ParameterType == grainId.GetType()
                                  && w.GetParameters()[2].ParameterType == typeof(string));
        }
    }
}