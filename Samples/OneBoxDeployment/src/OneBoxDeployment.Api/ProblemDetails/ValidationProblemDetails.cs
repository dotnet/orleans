using System.Collections.Generic;

namespace OneBoxDeployment.Api.ProblemDetails
{
    /// <summary>
    /// An instance of one validation error in ASP.NET Core model validation pipeline.
    /// </summary>
    public class ValidationError
    {
        /// <summary>
        /// The name of the error.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// The description of the error.
        /// </summary>
        public string Description { get; set; }
    }


    /// <summary>
    /// A collection of validation errors in ASP.NET Core model validation pipeline.
    /// These are used to add RFC 7807 details automatically.
    /// </summary>
    /// <remarks>See more at <a href="https://github.com/aspnet/Mvc/blob/master/src/Microsoft.AspNetCore.Mvc.Core/ProblemDetails.cs">ProblemDetails</a>.</remarks>
    public class ValidationProblemDetails: Microsoft.AspNetCore.Mvc.ProblemDetails
    {
        /// <summary>
        /// Collection of validation errors from the ASP.NET Core validation pipeline.
        /// </summary>
        public ICollection<ValidationError> ValidationErrors { get; set; }
    }
}
