<!-- Azure credential chain guidance -->

> [!TIP]
> <xref:Azure.Identity.DefaultAzureCredential> works seamlessly across local development and production. In development, it uses your Azure CLI or Visual Studio credentials. In production on Azure, it automatically uses the resource's managed identity. For improved performance and debuggability in production, consider replacing it with a specific credential like <xref:Azure.Identity.ManagedIdentityCredential>. For more information, see [Usage guidance for DefaultAzureCredential](../../azure/sdk/authentication/credential-chains.md#usage-guidance-for-defaultazurecredential).
