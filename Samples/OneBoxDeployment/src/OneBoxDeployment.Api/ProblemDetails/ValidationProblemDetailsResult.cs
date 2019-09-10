using OneBoxDeployment.Api.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OneBoxDeployment.Api.ProblemDetails
{
    /// <summary>
    /// Projects <see cref="ModelStateDictionary"/> to a collection of <see cref="ValidationProblemDetails"/> instances,
    /// allowing ASP.NET Core validation results to be added as <em>extension members</em> to the standard properties.
    /// </summary>
    public class ValidationProblemDetailsResult: IActionResult
    {
        /// <inheritdoc />
        public Task ExecuteResultAsync(ActionContext context)
        {
            var modelStateEntries = context.ModelState.Where(e => e.Value.Errors.Count > 0).ToArray();
            var errors = new List<ValidationError>();
            var details = "See ValidationErrors for details";
            if(modelStateEntries.Length > 0)
            {
                if(modelStateEntries.Length == 1 && modelStateEntries[0].Value.Errors.Count == 1 && modelStateEntries[0].Key?.Length == 0)
                {
                    details = modelStateEntries[0].Value.Errors[0].ErrorMessage;
                }
                else
                {
                    errors = modelStateEntries.SelectMany(modelStateEntry => modelStateEntry.Value.Errors.Select(modelStateError => new ValidationError
                    {
                        Name = modelStateEntry.Key,
                        Description = modelStateError.ErrorMessage
                    })).ToList();
                }
            }

            var problemDetails = new ValidationProblemDetails
            {
                Status = 400,
                Title = "Request Validation Error",
                Instance = $"urn:oneboxdeployment:badrequest:{Guid.NewGuid()}",
                Detail = details,
                ValidationErrors = errors
            };

            context.HttpContext.Response.WriteJson(problemDetails);
            return Task.CompletedTask;
        }
    }
}
