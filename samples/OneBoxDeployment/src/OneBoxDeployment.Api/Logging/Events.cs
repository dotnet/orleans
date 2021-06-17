using Microsoft.Extensions.Logging;

namespace OneBoxDeployment.Api.Logging
{
    /// <summary>
    /// Events used to log application activities.
    /// </summary>
    public static class Events
    {
        /// <summary>
        /// Signals in startup the address in which Swagger documentation is available.
        /// This is mainly to aid developers to look into the right place when the application starts.
        /// </summary>
        public static EventStruct SwaggerDocumentation { get; } = new EventStruct(new EventId(1, nameof(SwaggerDocumentation)), "To see API information, open browser in {swaggerUrl}.");

        /// <summary>
        /// An uncaught exception was caught in global exception handler.
        /// </summary>
        public static EventStruct GlobalExceptionHandler { get; } = new EventStruct(new EventId(2, nameof(GlobalExceptionHandler)), "Unhandled exception caught in global exception handler: {exception}.");

        /// <summary>
        /// A test event for OneBoxDeployment.
        /// </summary>
        public static EventStruct TestEvent { get; } = new EventStruct(new EventId(10001, nameof(TestEvent)), "A test event: {info}.");
    }
}
