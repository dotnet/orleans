using Orleans.Runtime;

namespace Orleans.Docs.Snippets.Interceptors;

// <exception_conversion_filter>
public class ExceptionConversionFilter : Orleans.IIncomingGrainCallFilter
{
    private static readonly HashSet<string> KnownExceptionTypeAssemblyNames =
        new HashSet<string>
        {
            typeof(string).Assembly.GetName().Name!,
            "System",
            "System.ComponentModel.Composition",
            "System.ComponentModel.DataAnnotations",
            "System.Configuration",
            "System.Core",
            "System.Data",
            "System.Data.DataSetExtensions",
            "System.Net.Http",
            "System.Numerics",
            "System.Runtime.Serialization",
            "System.Security",
            "System.Xml",
            "System.Xml.Linq",
            "MyCompany.Microservices.DataTransfer",
            "MyCompany.Microservices.Interfaces",
            "MyCompany.Microservices.ServiceLayer"
        };

    public async Task Invoke(Orleans.IIncomingGrainCallContext context)
    {
        var isConversionEnabled =
            RequestContext.Get("IsExceptionConversionEnabled") as bool? == true;

        if (!isConversionEnabled)
        {
            // If exception conversion is not enabled, execute the call without interference.
            await context.Invoke();
            return;
        }

        RequestContext.Remove("IsExceptionConversionEnabled");
        try
        {
            await context.Invoke();
        }
        catch (Exception exc)
        {
            var type = exc.GetType();

            if (KnownExceptionTypeAssemblyNames.Contains(
                type.Assembly.GetName().Name!))
            {
                throw;
            }

            // Throw a base exception containing some exception details.
            throw new Exception(
                string.Format(
                    "Exception of non-public type '{0}' has been wrapped."
                    + " Original message: <<<<----{1}{2}{3}---->>>>",
                    type.FullName,
                    Environment.NewLine,
                    exc,
                    Environment.NewLine));
        }
    }
}
// </exception_conversion_filter>
