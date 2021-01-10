using System.Net.Http;
using System.Net.Mime;
using System.Text;
using System.Text.Json;

namespace OneBoxDeployment.IntegrationTests.Dtos
{
    /// <summary>
    /// Transform the object into JSON and adds appropriate accept headers.
    /// </summary>
    /// <remarks>May not be needed later,
    /// see at https://docs.microsoft.com/en-us/dotnet/api/system.net.http.json.jsoncontent?view=net-5.0. </remarks>
    public sealed class JsonContent: StringContent
    {
        /// <summary>
        /// A default constructor.
        /// </summary>
        /// <param name="content">The content to transform into JSON.</param>
        public JsonContent(object content): base(JsonSerializer.Serialize(content), Encoding.UTF8, MediaTypeNames.Application.Json)
        { }
    }
}
