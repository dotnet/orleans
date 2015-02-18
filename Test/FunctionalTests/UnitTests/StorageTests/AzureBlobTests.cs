using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.WindowsAzure;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Orleans.Runtime;

namespace UnitTests.StorageTests
{
    [TestClass]
    public class AzureBlobTests
    {
        private static readonly SafeRandom rng = new SafeRandom();

        [TestMethod, TestCategory("Azure"), TestCategory("Blob")]
        public async Task Blob_AC_FromAsync_WriteAzureBlob_Sync_CreateIfNotExist()
        {
            Guid deploymentId = Guid.NewGuid();
            await Test_FromAsync_WriteAzureBlob(deploymentId, false);
        }

        [TestMethod, TestCategory("Azure"), TestCategory("Blob")]
        public async Task Blob_AC_FromAsync_WriteAzureBlob_Async_CreateIfNotExist()
        {
            Guid deploymentId = Guid.NewGuid();
            await Test_FromAsync_WriteAzureBlob(deploymentId, true);
        }

        [TestMethod, TestCategory("Azure"), TestCategory("Blob")]
        public async Task Blob_AC_FromAsync_Perf_WriteAzureBlob_Sync_CreateIfNotExist()
        {
            const string testName = "AC_FromAsync_Perf_WriteAzureBlob_Sync_CreateIfNotExist";
            Guid deploymentId = Guid.NewGuid();
            Stopwatch sw = new Stopwatch();
            sw.Start();
            for (int i = 0; i < 100; i++)
            {
                await Test_FromAsync_WriteAzureBlob(deploymentId, false);
            }
            sw.Stop();
            Console.WriteLine("{0} - Elapsed time = {1}", testName, sw.Elapsed);
        }

        [TestMethod, TestCategory("Azure"), TestCategory("Blob")]
        public async Task Blob_AC_FromAsync_Perf_WriteAzureBlob_Async_CreateIfNotExist()
        {
            const string testName = "AC_FromAsync_Perf_WriteAzureBlob_Async_CreateIfNotExist";
            Guid deploymentId = Guid.NewGuid();
            Stopwatch sw = new Stopwatch();
            sw.Start();
            for (int i = 0; i < 100; i++)
            {
                await Test_FromAsync_WriteAzureBlob(deploymentId, true);
            }
            sw.Stop();
            Console.WriteLine("{0} - Elapsed time = {1}", testName, sw.Elapsed);
        }

        private async Task Test_FromAsync_WriteAzureBlob(Guid deploymentId, bool asyncCreateIfNotExist)
        {
            long xuid = rng.Next();

            var imgFile = new FileInfo(@"orleans-ball-64.png");

            string blobName = GetBlobName(xuid, deploymentId);
            string containerName = GetContainerName(xuid, deploymentId);

            CloudBlobClient blobClient = GetBlobClient(TestConstants.DataConnectionString);
            CloudBlobContainer container = blobClient.GetContainerReference(containerName);

            if (asyncCreateIfNotExist)
            {
                bool didCreate = await Task.Factory.FromAsync(
                    container.BeginCreateIfNotExists,
                    cb => container.EndCreateIfNotExists(cb),
                    null);
            }
            else
            {
                container.CreateIfNotExists();
            }

            bool ok = await WriteToBlogStorage(container, blobName, imgFile);
            Assert.IsTrue(ok, "Upload should complete succesfully");
        }

        private static async Task<bool> WriteToBlogStorage(CloudBlobContainer container, string blobName, FileInfo imgFile)
        {
            byte[] img = File.ReadAllBytes(imgFile.FullName);

            var blob = container.GetBlobReferenceFromServer(blobName);
            blob.Properties.ContentType = "image/" + imgFile.Extension.Substring(1);


            using (MemoryStream stream = new MemoryStream(img))
            {
                await Task.Factory.FromAsync((cb, s) => blob.BeginUploadFromStream(stream, cb, s), blob.EndUploadFromStream, null);
                return true;
            }
        }

        private static CloudBlobClient GetBlobClient(string storageAccountConnectionString)
        {
            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(storageAccountConnectionString);
            return storageAccount.CreateCloudBlobClient();
        }

        private static string GetContainerName(long xuid, Guid deploymentId)
        {
            return String.Format("{0}-SpartanImage", deploymentId).ToLower();
        }

        private static string GetBlobName(long xuid, Guid deploymentId)
        {
            return String.Format("{0:X}/player_image.png", xuid);
        }
    }
}

