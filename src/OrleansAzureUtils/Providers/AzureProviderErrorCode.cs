namespace Orleans.Providers.Azure
{
    internal enum AzureProviderErrorCode
    {
        ProvidersBase = 200000,
        
        // Azure storage provider related
        AzureTableProviderBase                      = ProvidersBase + 100,
        AzureTableProvider_DataNotFound             = AzureTableProviderBase + 1,
        AzureTableProvider_ReadingData              = AzureTableProviderBase + 2,
        AzureTableProvider_WritingData              = AzureTableProviderBase + 3,
        AzureTableProvider_Storage_Reading          = AzureTableProviderBase + 4,
        AzureTableProvider_Storage_Writing          = AzureTableProviderBase + 5,
        AzureTableProvider_Storage_DataRead         = AzureTableProviderBase + 6,
        AzureTableProvider_WriteError               = AzureTableProviderBase + 7,
        AzureTableProvider_DeleteError              = AzureTableProviderBase + 8,
        AzureTableProvider_InitProvider             = AzureTableProviderBase + 9,
        AzureTableProvider_ParamConnectionString    = AzureTableProviderBase + 10,

        AzureBlobProviderBase                       = ProvidersBase + 300,
        AzureBlobProvider_BlobNotFound              = AzureBlobProviderBase + 1,
        AzureBlobProvider_ContainerNotFound         = AzureBlobProviderBase + 2,
        AzureBlobProvider_BlobEmpty                 = AzureBlobProviderBase + 3,
        AzureBlobProvider_ReadingData               = AzureBlobProviderBase + 4,
        AzureBlobProvider_WritingData               = AzureBlobProviderBase + 5,
        AzureBlobProvider_Storage_Reading           = AzureBlobProviderBase + 6,
        AzureBlobProvider_Storage_Writing           = AzureBlobProviderBase + 7,
        AzureBlobProvider_Storage_DataRead          = AzureBlobProviderBase + 8,
        AzureBlobProvider_WriteError                = AzureBlobProviderBase + 9,
        AzureBlobProvider_DeleteError               = AzureBlobProviderBase + 10,
        AzureBlobProvider_InitProvider              = AzureBlobProviderBase + 11,
        AzureBlobProvider_ParamConnectionString     = AzureBlobProviderBase + 12,
        AzureBlobProvider_ReadError                 = AzureBlobProviderBase + 13,
        AzureBlobProvider_ClearError                = AzureBlobProviderBase + 14,
        AzureBlobProvider_ClearingData              = AzureBlobProviderBase + 15,
        AzureBlobProvider_Cleared                   = AzureBlobProviderBase + 16,



    }
}
