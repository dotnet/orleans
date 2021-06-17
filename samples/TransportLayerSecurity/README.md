# Transport Layer Security (TLS) Sample

This sample demonstrates a client and silo which communicate over a channel secured by mutual Transport Layer Security (mTLS).

The key parts to this sample are:

* Generating a self-signed certificate (a CA-issued certificate can be used instead)
* Configuring the server and client to use mutual-TLS for authenticating connections.

The important difference from other samples is the `ISiloBuilder.UseTls(...)` in [`Program.cs`](./TLS.Server/Program.cs) on the server and `IClientBuilder.UseTls` on the client:

``` C#
siloBuilder.UseTls(
    StoreName.My,
    "fakedomain.faketld",
    allowInvalid: isDevelopment,
    StoreLocation.CurrentUser,
    options =>
    {
        // In this sample there is only one server, however if there are multiple silos then the TargetHost must be set
        // for each connection which is initiated.
        options.OnAuthenticateAsClient = (connection, sslOptions) =>
        {
            sslOptions.TargetHost = "fakedomain.faketld";
        };

        if (isDevelopment)
        {
            // NOTE: Do not do this in a production environment
            options.AllowAnyRemoteCertificate();
        }
    })
```

## Running the sample

For the purpose of the sample, we will generate and use a self-signed certificate.

***NOTE:*** Ensure that security best practices are followed when deploying your application to a production environment.

A self-signed certificate can be generated & installed using PowerShell:

``` powershell
$cert = New-SelfSignedCertificate -CertStoreLocation Cert:\CurrentUser\My -DnsName "fakedomain.faketld"
```

Now that the certificate configured in the sample is installed, run the client and silo:

Start the silo using the following command:

``` powershell
dotnet run --project TLS.Server
```

Start the client in a different command window using the following command:

``` powershell
dotnet run --project TLS.Client
```

Once you have successfully run the sample, remove the self-signed certificate which was generated above:

``` powershell
Remove-Item "Cert:\CurrentUser\My\$($cert.ThumbPrint)"
```