namespace Orleans.CodeGeneration
{
    public class Program
    {
        public static int Main(string[] args)
        {
            var generator = new GrainClientGenerator();
            return generator.RunMain(args);
        }
    }
}
