using System.Net.Http;
using System.Text;
using Newtonsoft.Json;

namespace OneBoxDeployment.IntegrationTests.Dtos
{
    /// <summary>
    /// Transform the object into JSON and adds appropriate accept headers.
    /// </summary>
    public class JsonContent: StringContent
    {
        /// <summary>
        /// A default constructor.
        /// </summary>
        /// <param name="content">The content to transform into JSON.</param>
        public JsonContent(object content): base(JsonConvert.SerializeObject(content), Encoding.UTF8, "application/json")
        { }
    }
}
