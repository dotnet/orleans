# Transport Layer Security (TLS) Sample

This sample demonstrates a client and silo which communicate over a channel secured by Transport Layer Security (TLS).

## Running the sample

For the purpose of the sample, we will generate and use a self-signed certificate.

***NOTE:*** Ensure that security best practices are followed when deploying your application to a production environment.

A self-signed certificate can be generated & installed using PowerShell:

``` powershell
$cert = New-SelfSignedCertificate -CertStoreLocation Cert:\LocalMachine\My -DnsName "fakedomain.faketld"
```

Now that the certificate configured in the sample is installed, run the client and silo:

Start the silo using the following command:

``` powershell
dotnet run --project src\SiloHost
```

Start the client in a different command window using the following command:

``` powershell
dotnet run --project src\OrleansClient\
```

Once you have successfully run the sample, remove the self-signed certificate which was generated above:

``` powershell
Remove-Item "Cert:\LocalMachine\My\$($cert.ThumbPrint)"
```