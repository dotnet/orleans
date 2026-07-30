---
languages:
- csharp
products:
- dotnet
- dotnet-orleans
page_type: sample
name: "Orleans Transport Layer Security (TLS) sample"
urlFragment: "orleans-transport-layer-security-tls"
description: "An Orleans sample demonstrating transport layer security (TLS)."
---

# Orleans Transport Layer Security (TLS) sample

This sample demonstrates a client and silo which communicate over a channel secured by mutual Transport Layer Security (mTLS).

## Running the sample

The sample automatically creates a self-signed certificate if one doesn't exist. Simply run the sample:

```powershell
.\run.cmd
```

Or on Linux/macOS:

```bash
./run.sh
```

The sample will:

1. Check if a certificate named `fakedomain.faketld` exists in the user's certificate store
2. If not found, automatically create a self-signed certificate and store it
3. Start the silo and client using TLS for all communication

## Key concepts

The important parts of this sample are:

* Automatic self-signed certificate generation for development
* Configuring the server and client to use mutual-TLS for authenticating connections

### Certificate management

The `CertificateHelper` class in `TLS.Contracts` handles certificate management:

```csharp
// Automatically ensures a certificate exists (creates if missing)
CertificateHelper.EnsureCertificateExists();
```

### TLS configuration

The `ISiloBuilder.UseTls(...)` in [`Program.cs`](./TLS.Server/Program.cs) on the server and `IClientBuilder.UseTls` on the client configure TLS:

```csharp
builder.UseTls(
    CertificateHelper.StoreName,
    CertificateHelper.CertificateSubject,
    allowInvalid: isDevelopment,
    CertificateHelper.StoreLocation,
    options =>
    {
        // In this sample there is only one server, however if there are multiple silos then the TargetHost must be set
        // for each connection which is initiated.
        options.OnAuthenticateAsClient = (connection, sslOptions) =>
        {
            sslOptions.TargetHost = CertificateHelper.CertificateSubject;
        };

        if (isDevelopment)
        {
            // NOTE: Do not do this in a production environment
            options.AllowAnyRemoteCertificate();
        }
    })
```

## Manual certificate management

### Creating a certificate manually (optional)

If you prefer to create the certificate manually using PowerShell:

```powershell
$cert = New-SelfSignedCertificate -CertStoreLocation Cert:\CurrentUser\My -DnsName "fakedomain.faketld"
```

### Removing the certificate

To remove the self-signed certificate after running the sample:

```powershell
Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -like "*fakedomain.faketld*" } | Remove-Item
```

## Sample prerequisites

This sample is written in C# and targets .NET 10. It requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or later.

## Building the sample

To download and run the sample, follow these steps:

1. Download and unzip the sample.
2. In Visual Studio (2022 or later):
    1. On the menu bar, choose **File** > **Open** > **Project/Solution**.
    2. Navigate to the folder that holds the unzipped sample code, and open the solution file.
    3. Choose the <kbd>F5</kbd> key to run with debugging, or <kbd>Ctrl</kbd>+<kbd>F5</kbd> keys to run the project without debugging.
3. From the command line:
   1. Navigate to the folder that holds the unzipped sample code.
   2. Run `.\run.cmd` (Windows) or `./run.sh` (Linux/macOS).

***NOTE:*** Ensure that security best practices are followed when deploying your application to a production environment. Use CA-issued certificates and do not allow invalid certificates in production.
