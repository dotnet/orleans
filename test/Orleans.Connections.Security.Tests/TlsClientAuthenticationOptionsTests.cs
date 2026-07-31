using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Orleans.Connections.Security;
using Xunit;

namespace Orleans.Connections.Security.Tests;

public class TlsClientAuthenticationOptionsTests
{
    [Fact]
    public void LocalCertificateSelectionCallback_CanReturnNull()
    {
        var options = new TlsClientAuthenticationOptions
        {
            LocalCertificateSelectionCallback = static (_, _, _, _, _) => null
        };

        var callback = Assert.IsType<SslClientAuthenticationOptions>(options.SslClientAuthenticationOptions)
            .LocalCertificateSelectionCallback;
        Assert.NotNull(callback);
        Assert.Null(callback(new object(), "localhost", new X509CertificateCollection(), null, []));
    }
}
