using System;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Orleans.Dashboard;
using Xunit;

namespace UnitTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Dashboard")]
    [TestCategory("BVT")]
    public class EmbeddedAssetTests
    {
        private static readonly Assembly DashboardAssembly = typeof(DashboardOptions).Assembly;
        private const string ResourcePrefix = "Orleans.Dashboard.wwwroot.";

        [Fact]
        public void Assembly_ContainsEmbeddedResources()
        {
            var resourceNames = DashboardAssembly.GetManifestResourceNames();

            Assert.NotEmpty(resourceNames);
        }

        [Fact]
        public void Assembly_ContainsIndexHtml()
        {
            var resourceName = $"{ResourcePrefix}index.html";

            var resourceNames = DashboardAssembly.GetManifestResourceNames();

            Assert.Contains(resourceName, resourceNames);
        }

        [Fact]
        public void Assembly_ContainsIndexCss()
        {
            var resourceName = $"{ResourcePrefix}index.css";

            var resourceNames = DashboardAssembly.GetManifestResourceNames();

            Assert.Contains(resourceName, resourceNames);
        }

        [Fact]
        public void Assembly_ContainsIndexJs()
        {
            var resourceName = $"{ResourcePrefix}index.min.js";

            var resourceNames = DashboardAssembly.GetManifestResourceNames();

            Assert.Contains(resourceName, resourceNames);
        }

        [Fact]
        public void Assembly_ContainsFavicon()
        {
            var resourceName = $"{ResourcePrefix}favicon.ico";

            var resourceNames = DashboardAssembly.GetManifestResourceNames();

            Assert.Contains(resourceName, resourceNames);
        }

        [Fact]
        public void Assembly_ContainsFontFiles()
        {
            var resourceNames = DashboardAssembly.GetManifestResourceNames();
            var fontResources = resourceNames.Where(n => n.StartsWith($"{ResourcePrefix}fonts.", StringComparison.Ordinal));

            Assert.NotEmpty(fontResources);
        }

        [Fact]
        public void IndexHtml_IsNotEmpty()
        {
            var resourceName = $"{ResourcePrefix}index.html";

            using var stream = DashboardAssembly.GetManifestResourceStream(resourceName);

            Assert.NotNull(stream);
            Assert.True(stream.Length > 0, "index.html should not be empty");
        }

        [Fact]
        public void IndexCss_IsNotEmpty()
        {
            var resourceName = $"{ResourcePrefix}index.css";

            using var stream = DashboardAssembly.GetManifestResourceStream(resourceName);

            Assert.NotNull(stream);
            Assert.True(stream.Length > 0, "index.css should not be empty");
        }

        [Fact]
        public void IndexJs_IsNotEmpty()
        {
            var resourceName = $"{ResourcePrefix}index.min.js";

            using var stream = DashboardAssembly.GetManifestResourceStream(resourceName);

            Assert.NotNull(stream);
            Assert.True(stream.Length > 0, "index.min.js should not be empty");
        }

        [Fact]
        public void IndexHtml_ContainsExpectedContent()
        {
            var resourceName = $"{ResourcePrefix}index.html";

            using var stream = DashboardAssembly.GetManifestResourceStream(resourceName);
            // The resource's presence is verified by Assembly_ContainsIndexHtml.
            using var reader = new System.IO.StreamReader(stream!);
            var content = reader.ReadToEnd();

            Assert.Contains("<!DOCTYPE html>", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Orleans Dashboard", content);
            Assert.Contains("index.min.js", content);
            Assert.Contains("index.css", content);
        }

        [Fact]
        public async System.Threading.Tasks.Task EmbeddedAssetProvider_CanBeInstantiated()
        {
            var provider = new EmbeddedAssetProvider();
            using var requestServices = CreateRequestServices();
            var httpContext = CreateHttpContext(requestServices);

            var result = provider.ServeAsset("INDEX.HTML", httpContext);
            await result.ExecuteAsync(httpContext);

            Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
            Assert.Equal(ReadEmbeddedAsset("index.html"), ((System.IO.MemoryStream)httpContext.Response.Body).ToArray());
        }

        [Fact]
        public async System.Threading.Tasks.Task EmbeddedAssetProvider_ServesIndexHtml()
        {
            var provider = new EmbeddedAssetProvider();

            await AssertServesExactAsset(provider, "index.html");
        }

        [Fact]
        public async System.Threading.Tasks.Task EmbeddedAssetProvider_ServesIndexCss()
        {
            var provider = new EmbeddedAssetProvider();

            await AssertServesExactAsset(provider, "index.css");
        }

        [Fact]
        public async System.Threading.Tasks.Task EmbeddedAssetProvider_ServesIndexJs()
        {
            var provider = new EmbeddedAssetProvider();

            await AssertServesExactAsset(provider, "index.min.js");
        }

        [Fact]
        public async System.Threading.Tasks.Task EmbeddedAssetProvider_ServesFontFiles()
        {
            var provider = new EmbeddedAssetProvider();
            var fontResourceName = DashboardAssembly
                .GetManifestResourceNames()
                .FirstOrDefault(name =>
                    name.StartsWith(ResourcePrefix, StringComparison.Ordinal) &&
                    (name.EndsWith(".woff2", StringComparison.Ordinal) ||
                     name.EndsWith(".woff", StringComparison.Ordinal)));

            Assert.NotNull(fontResourceName);
            var assetName = fontResourceName.Substring(ResourcePrefix.Length);

            await AssertServesExactAsset(provider, assetName);
        }

        [Fact]
        public void EmbeddedAssetProvider_ReturnsNotFoundForMissingAsset()
        {
            var provider = new EmbeddedAssetProvider();
            var httpContext = new DefaultHttpContext();

            var result = provider.ServeAsset("nonexistent.file", httpContext);

            Assert.IsType<NotFound>(result);
        }

        [Fact]
        public async System.Threading.Tasks.Task ServeAsset_KnownAsset_ReturnsExactContentTypeCacheEtagAndBody()
        {
            var provider = new EmbeddedAssetProvider();
            var expectedBody = ReadEmbeddedAsset("index.html");
            using var requestServices = CreateRequestServices();
            var httpContext = CreateHttpContext(requestServices);

            var result = provider.ServeAsset("index.html", httpContext);
            await result.ExecuteAsync(httpContext);

            Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
            Assert.Equal("text/html", httpContext.Response.ContentType);
            Assert.Equal(expectedBody.Length, httpContext.Response.ContentLength);
            var cacheControl = httpContext.Response.GetTypedHeaders().CacheControl;
            Assert.NotNull(cacheControl);
            Assert.True(cacheControl.NoCache);
            Assert.True(cacheControl.NoStore);
            Assert.Equal(1, httpContext.Response.Headers.ETag.Count);
            Assert.Equal(0, httpContext.Response.Headers.ContentEncoding.Count);
            var actualBody = ((System.IO.MemoryStream)httpContext.Response.Body).ToArray();
            Assert.Equal(expectedBody, actualBody);
            Assert.Contains(
                "<!DOCTYPE html>",
                System.Text.Encoding.UTF8.GetString(actualBody),
                StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async System.Threading.Tasks.Task ServeAsset_GzipAccepted_ReturnsCompressedContentAndContentEncoding()
        {
            var provider = new EmbeddedAssetProvider();
            var expectedDecompressedBody = ReadEmbeddedAsset("index.html");
            using var requestServices = CreateRequestServices();
            var httpContext = CreateHttpContext(requestServices);
            httpContext.Request.Headers.AcceptEncoding = "br, GZIP; q=0.8";

            var result = provider.ServeAsset("index.html", httpContext);
            await result.ExecuteAsync(httpContext);

            var actualBody = ((System.IO.MemoryStream)httpContext.Response.Body).ToArray();
            Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
            Assert.Equal("text/html", httpContext.Response.ContentType);
            Assert.Equal(actualBody.Length, httpContext.Response.ContentLength);
            Assert.True(actualBody.Length < expectedDecompressedBody.Length);
            Assert.Equal("gzip", httpContext.Response.Headers.ContentEncoding.ToString());
            var cacheControl = httpContext.Response.GetTypedHeaders().CacheControl;
            Assert.NotNull(cacheControl);
            Assert.True(cacheControl.NoCache);
            Assert.True(cacheControl.NoStore);
            Assert.Equal(1, httpContext.Response.Headers.ETag.Count);
            Assert.Equal(expectedDecompressedBody, Decompress(actualBody));
        }

        [Fact]
        public async System.Threading.Tasks.Task ServeAsset_GzipQualityZero_ReturnsDecompressedRepresentationWithoutEncoding()
        {
            var provider = new EmbeddedAssetProvider();
            var expectedBody = ReadEmbeddedAsset("index.html");
            using var requestServices = CreateRequestServices();
            var httpContext = CreateHttpContext(requestServices);
            httpContext.Request.Headers.AcceptEncoding = "gzip; q=0, br";

            var result = provider.ServeAsset("index.html", httpContext);
            await result.ExecuteAsync(httpContext);

            Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
            Assert.Equal(expectedBody.Length, httpContext.Response.ContentLength);
            Assert.Equal(0, httpContext.Response.Headers.ContentEncoding.Count);
            Assert.Equal(1, httpContext.Response.Headers.ETag.Count);
            Assert.Equal(expectedBody, ((System.IO.MemoryStream)httpContext.Response.Body).ToArray());
        }

        [Fact]
        public async System.Threading.Tasks.Task ServeAsset_SlashSeparatedNestedAsset_ReturnsExactBody()
        {
            var provider = new EmbeddedAssetProvider();
            using var requestServices = CreateRequestServices();
            var httpContext = CreateHttpContext(requestServices);
            var expectedBody = ReadEmbeddedAsset("fonts.fa-solid-900.woff2");

            var result = provider.ServeAsset("fonts/fa-solid-900.woff2", httpContext);
            await result.ExecuteAsync(httpContext);

            Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
            Assert.Equal("font/woff2", httpContext.Response.ContentType);
            Assert.Equal(expectedBody, ((System.IO.MemoryStream)httpContext.Response.Body).ToArray());
        }

        [Fact]
        public async System.Threading.Tasks.Task ServeAsset_MatchingIfNoneMatch_Returns304WithoutBodyOrRepresentationHeaders()
        {
            var provider = new EmbeddedAssetProvider();
            using var requestServices = CreateRequestServices();
            var initialContext = CreateHttpContext(requestServices);
            var initialResult = provider.ServeAsset("index.html", initialContext);
            await initialResult.ExecuteAsync(initialContext);
            var entityTag = initialContext.Response.Headers.ETag;
            Assert.Equal(1, entityTag.Count);

            var httpContext = CreateHttpContext(requestServices);
            httpContext.Request.Headers.IfNoneMatch = entityTag;

            var result = provider.ServeAsset("index.html", httpContext);
            await result.ExecuteAsync(httpContext);

            Assert.Equal(StatusCodes.Status304NotModified, httpContext.Response.StatusCode);
            Assert.Null(httpContext.Response.ContentType);
            Assert.Null(httpContext.Response.ContentLength);
            Assert.Equal(0, httpContext.Response.Headers.CacheControl.Count);
            Assert.Equal(0, httpContext.Response.Headers.ETag.Count);
            Assert.Equal(0, httpContext.Response.Headers.ContentEncoding.Count);
            Assert.Empty(((System.IO.MemoryStream)httpContext.Response.Body).ToArray());
        }

        [Fact]
        public async System.Threading.Tasks.Task ServeAsset_UnknownAsset_Returns404WithoutBodyOrCacheHeaders()
        {
            var provider = new EmbeddedAssetProvider();
            using var requestServices = CreateRequestServices();
            var httpContext = CreateHttpContext(requestServices);

            var result = provider.ServeAsset("missing/asset.js", httpContext);
            await result.ExecuteAsync(httpContext);

            Assert.Equal(StatusCodes.Status404NotFound, httpContext.Response.StatusCode);
            Assert.Null(httpContext.Response.ContentType);
            Assert.Null(httpContext.Response.ContentLength);
            Assert.Equal(0, httpContext.Response.Headers.CacheControl.Count);
            Assert.Equal(0, httpContext.Response.Headers.ETag.Count);
            Assert.Equal(0, httpContext.Response.Headers.ContentEncoding.Count);
            Assert.Empty(((System.IO.MemoryStream)httpContext.Response.Body).ToArray());
        }

        private static DefaultHttpContext CreateHttpContext(System.IServiceProvider requestServices)
        {
            var context = new DefaultHttpContext
            {
                RequestServices = requestServices,
            };
            context.Response.Body = new System.IO.MemoryStream();
            return context;
        }

        private static Microsoft.Extensions.DependencyInjection.ServiceProvider CreateRequestServices()
        {
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            Microsoft.Extensions.DependencyInjection.LoggingServiceCollectionExtensions.AddLogging(services);
            return Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        }

        private static async System.Threading.Tasks.Task AssertServesExactAsset(
            EmbeddedAssetProvider provider,
            string assetName)
        {
            var expectedBody = ReadEmbeddedAsset(assetName);
            using var requestServices = CreateRequestServices();
            var httpContext = CreateHttpContext(requestServices);

            var result = provider.ServeAsset(assetName, httpContext);
            await result.ExecuteAsync(httpContext);

            Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
            Assert.Equal(expectedBody.Length, httpContext.Response.ContentLength);
            var cacheControl = httpContext.Response.GetTypedHeaders().CacheControl;
            Assert.NotNull(cacheControl);
            Assert.True(cacheControl.NoCache);
            Assert.True(cacheControl.NoStore);
            Assert.Equal(1, httpContext.Response.Headers.ETag.Count);
            Assert.Equal(expectedBody, ((System.IO.MemoryStream)httpContext.Response.Body).ToArray());
        }

        private static byte[] ReadEmbeddedAsset(string assetName)
        {
            using var stream = DashboardAssembly.GetManifestResourceStream($"{ResourcePrefix}{assetName}");
            Assert.NotNull(stream);
            using var output = new System.IO.MemoryStream();
            stream.CopyTo(output);
            return output.ToArray();
        }

        private static byte[] Decompress(byte[] body)
        {
            using var input = new System.IO.MemoryStream(body);
            using var gzip = new System.IO.Compression.GZipStream(
                input,
                System.IO.Compression.CompressionMode.Decompress);
            using var output = new System.IO.MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }
    }
}
