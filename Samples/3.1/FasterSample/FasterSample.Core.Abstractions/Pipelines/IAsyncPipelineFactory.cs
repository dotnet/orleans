namespace FasterSample.Core.Pipelines
{
    public interface IAsyncPipelineFactory
    {
        IAsyncPipeline Create(int capacity);
    }
}