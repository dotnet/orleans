namespace UnitTests.StorageTests.AWSUtils
{
    public class AWSTestConstants
    {
        public static string AccessKey { get; set; }
        public static string SecretKey { get; set; }
        public static string Service { get; set; }

        static AWSTestConstants()
        {
            Service = "http://localhost:8000";
        }
    }
}
