using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Filters;
using System;
using System.Linq;
using System.Reflection;

namespace OneBoxDeployment.Api.Filters
{
    /// <summary>
    /// An action, or method level, attribute to transform <em>null</em> values into
    /// <see cref="NotFoundResult"/>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class NotFoundResultActionFilterAttribute : ActionFilterAttribute
    {
        /// <inheritdoc />
        public override void OnActionExecuted(ActionExecutedContext context)
        {
            if(context.Result is ObjectResult objectResult && objectResult.Value == null)
            {
                context.Result = new NotFoundResult();
            }
        }
    }

    /// <summary>
    /// A filter that turns <em>null</em> return values into <see cref="NotFoundResult"/> late
    /// enough in the pipeline. This considers also the case any middleware will return
    /// <see cref="ObjectResult"/> with <em>null</em> content.
    /// </summary>
    [AttributeUsage(AttributeTargets.All, AllowMultiple = false, Inherited = true)]
    public sealed class NotFoundResultFilterAttribute: Attribute, IAlwaysRunResultFilter
    {
        /// <inheritdoc />
        public void OnResultExecuted(ResultExecutedContext context) { }

        /// <inheritdoc />
        public void OnResultExecuting(ResultExecutingContext context)
        {
            if(context.Result is ObjectResult objectResult && objectResult.Value == null)
            {
                context.Result = new NotFoundResult();
            }
        }
    }

    /// <summary>
    /// Adds a convention to transform <em>null</em> values into <see cref="NotFoundResult"/>
    /// to controllers that are marked with <see cref="ApiControllerAttribute"/>.
    /// </summary>
    public class NotFoundResultFilterConvention: IControllerModelConvention
    {
        /// <summary>
        /// Applies <see cref="NotFoundResultFilterAttribute"/> by convention to
        /// controllers with <em>ApiController</em> attribute.
        /// </summary>
        /// <param name="controller"></param>
        public void Apply(ControllerModel controller)
        {
            if(IsApiController(controller))
            {
                var typeFilters = controller.Filters.Where(f => f.GetType() == typeof(TypeFilterAttribute)).Cast<TypeFilterAttribute>();
                if (typeFilters.All(t => t.ImplementationType != typeof(NotFoundResultFilterAttribute)))
                {
                    controller.Filters.Add(
                        new TypeFilterAttribute(typeof(NotFoundResultFilterAttribute))
                        {
                            Order = 1000
                        });
                }
            }
        }


        private static bool IsApiController(ControllerModel controller)
        {
            if(controller.Attributes.OfType<ApiControllerAttribute>().Any())
            {
                return true;
            }

            return controller.ControllerType.Assembly.GetCustomAttributes().OfType<ApiControllerAttribute>().Any();
        }
    }
}
