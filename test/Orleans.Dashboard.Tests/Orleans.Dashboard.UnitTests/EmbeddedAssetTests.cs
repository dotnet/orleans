using System;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Orleans.Dashboard;
using Xunit;

namespace UnitTests
{
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
            using var reader = new System.IO.StreamReader(stream!);
            var content = reader.ReadToEnd();

            Assert.Contains("<!DOCTYPE html>", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Orleans Dashboard", content);
            Assert.Contains("index.min.js", content);
            Assert.Contains("index.css", content);
        }

        [Fact]
        public void EmbeddedAssetProvider_CanBeInstantiated()
        {
            // This tests that the EmbeddedAssetProvider can load all resources
            // without throwing exceptions during construction
            var provider = new EmbeddedAssetProvider();

            Assert.NotNull(provider);
        }

        [Fact]
        public void EmbeddedAssetProvider_ServesIndexHtml()
        {
            var provider = new EmbeddedAssetProvider();
            var httpContext = new DefaultHttpContext();

            var result = provider.ServeAsset("index.html", httpContext);

            // Result should not be NotFound - it should be a file result
            Assert.IsNotType<NotFound>(result);
        }

        [Fact]
        public void EmbeddedAssetProvider_ServesIndexCss()
        {
            var provider = new EmbeddedAssetProvider();
            var httpContext = new DefaultHttpContext();

            var result = provider.ServeAsset("index.css", httpContext);

            Assert.IsNotType<NotFound>(result);
        }

        [Fact]
        public void EmbeddedAssetProvider_ServesIndexJs()
        {
            var provider = new EmbeddedAssetProvider();
            var httpContext = new DefaultHttpContext();

            var result = provider.ServeAsset("index.min.js", httpContext);

            Assert.IsNotType<NotFound>(result);
        }

        [Fact]
        public void EmbeddedAssetProvider_ServesFontFiles()
        {
            var provider = new EmbeddedAssetProvider();
            var httpContext = new DefaultHttpContext();
            var assembly = typeof(EmbeddedAssetProvider).GetTypeInfo().Assembly;
            var fontResourceName = assembly
                .GetManifestResourceNames()
                .FirstOrDefault(name =>
                    name.EndsWith(".woff2", StringComparison.Ordinal) ||
                    name.EndsWith(".woff", StringComparison.Ordinal));

            Assert.NotNull(fontResourceName);

            var resourcePrefix = typeof(EmbeddedAssetProvider).Namespace + ".";
            var assetName = fontResourceName.StartsWith(resourcePrefix, StringComparison.Ordinal)
                ? fontResourceName.Substring(resourcePrefix.Length)
                : fontResourceName;

            var result = provider.ServeAsset(assetName, httpContext);
            Assert.IsNotType<NotFound>(result);
        }

        [Fact]
        public void EmbeddedAssetProvider_ReturnsNotFoundForMissingAsset()
        {
            var provider = new EmbeddedAssetProvider();
            var httpContext = new DefaultHttpContext();

            var result = provider.ServeAsset("nonexistent.file", httpContext);

            Assert.IsType<NotFound>(result);
        }
    }
}
