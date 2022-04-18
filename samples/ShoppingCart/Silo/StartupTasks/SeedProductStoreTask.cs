namespace Orleans.ShoppingCart.Silo.StartupTasks
{
    public sealed class SeedProductStoreTask : IStartupTask
    {
        readonly IGrainFactory _grainFactory;

        public SeedProductStoreTask(IGrainFactory grainFactory) =>
            _grainFactory = grainFactory;

        async Task IStartupTask.Execute(CancellationToken cancellationToken)
        {            
            var faker = new ProductDetails().GetBogusFaker();

            foreach (var product in faker.GenerateLazy(50))
            {
                var productGrain = _grainFactory.GetGrain<IProductGrain>(product.Id);
                await productGrain.CreateOrUpdateProductAsync(product);
            }
        }
    }
}
