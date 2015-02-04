/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

﻿
namespace Orleans.Providers
{
    internal enum ProviderErrorCode
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

        ShardedStorageProviderBase                  = ProvidersBase + 200,
        ShardedStorageProvider_ProviderName         = ShardedStorageProviderBase + 1,
        ShardedStorageProvider_HashValueOutOfBounds = ShardedStorageProviderBase + 2,
    }
}