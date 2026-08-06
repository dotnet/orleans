### Create a service principal

To automate the app's deployment, you need to create a service principal. This is a Microsoft account that has permission to manage Azure resources on your behalf.

```azurecli
az ad sp create-for-rbac --sdk-auth --role Contributor \
  --name "<display-name>"  --scopes /subscriptions/<your-subscription-id>
```

The JSON credentials created look similar to the following, but with actual values for your client, subscription, and tenant:

```json
{
  "clientId": "<your client id>",
  "clientSecret": "<your client secret>",
  "subscriptionId": "<your subscription id>",
  "tenantId": "<your tenant id>",
  "activeDirectoryEndpointUrl": "https://login.microsoftonline.com/",
  "resourceManagerEndpointUrl": "https://brazilus.management.azure.com",
  "activeDirectoryGraphResourceId": "https://graph.windows.net/",
  "sqlManagementEndpointUrl": "https://management.core.windows.net:8443/",
  "galleryEndpointUrl": "https://gallery.azure.com",
  "managementEndpointUrl": "https://management.core.windows.net"
}
```

Copy the output of the command into your clipboard, and continue to the next step.
