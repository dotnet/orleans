using Microsoft.AspNetCore.Http;
using Microsoft.OpenApi.Models;
using OneBoxDeployment.Common;
using Swashbuckle.AspNetCore.SwaggerGen;
using System.Collections.Generic;

namespace OneBoxDeployment.Api.Filters
{
    /// <summary>
    /// OpenAPI (Swagger) filter to have calls resulting to
    /// <see cref="StatusCodes.Status400BadRequest"/> to return
    /// <see cref="MimeTypes.ProblemDetailJsonMimeType"/>.
    /// </summary>
    public class BadRequestProblemDetailOpenApiFilterAttribute: IOperationFilter
    {
        /// <inheritdoc />
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            const string BadRequestHttpCode = "400";
            if(operation.Responses.ContainsKey(BadRequestHttpCode))
            {
                operation.Responses.Clear();
            }

            var data = new OpenApiResponse
            {
                Description = "Bad Request",
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    [MimeTypes.ProblemDetailJsonMimeType] = new OpenApiMediaType(),
                }
            };
            operation.Responses.Add(BadRequestHttpCode, data);
        }
    }
}
