using System.ComponentModel.DataAnnotations;
using FasterSample.Core.Clocks;
using FasterSample.Core.Pipelines;
using FasterSample.Core.RandomGenerators;
using Microsoft.AspNetCore.Components;
using Orleans;

namespace FasterSample.WebApp.Pages
{
    public partial class LoadTest
    {
        [Inject]
        public IGrainFactory GrainFactory { get; set; }

        [Inject]
        public IRandomGenerator RandomGenerator { get; set; }

        [Inject]
        public ISystemClock SystemClock { get; set; }

        [Inject]
        public IAsyncPipelineFactory AsyncPipelineFactory { get; set; }

        private readonly Model _model = new Model();

        private class Model
        {
            [Required]
            [Range(1, int.MaxValue)]
            public int ShardCount { get; set; } = 10;

            [Required]
            [Range(1, int.MaxValue)]
            public int KeyCount { get; set; } = 10;

            [Required]
            [Range(1, int.MaxValue)]
            public int ParallelRequestCount { get; set; } = 10;

            [Required]
            [Range(1, int.MaxValue)]
            public int PayloadItemCount { get; set; } = 10;

            [Required]
            [Range(0.0, 100.0)]
            public double ReadToWritePercent { get; set; } = 10;
        }

        private enum Status
        {
            Stopped,
            Changing,
            Running,
        }
    }
}