<!-- Azure credential chain guidance -->

> [!TIP]
> <xref:Azure.Identity.DefaultAzureCredential> works seamlessly across local development and production. In development, it uses your Azure CLI or Visual Studio credentials. In production on Azure, it automatically uses the resource's managed identity. For improved performance and debuggability in production, consider replacing it with a specific credential like <xref:Azure.Identity.ManagedIdentityCredential>. For more information, see [Usage guidance for DefaultAzureCredential](https://learn.microsoft.com/dotnet/azure/sdk/authentication/credential-chains#usage-guidance-for-defaultazurecredential).
