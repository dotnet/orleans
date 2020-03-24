namespace Orleans.Persistence.S3.Options {
    public class S3StorageOptions
    {
        public const int DEFAULT_INIT_STAGE = ServiceLifecycleStage.ApplicationServices;

        public int InitStage { get; set; } = DEFAULT_INIT_STAGE;
        public string BucketName { get; set; }
    }
}