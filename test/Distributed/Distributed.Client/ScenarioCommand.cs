using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Distributed.GrainInterfaces;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Distributed.Client
{
    public class ScenarioCommand<T> : Command
    {
        private readonly ScenarioRunner<T> _runner;

        public ScenarioCommand(IScenario<T> scenario)
            : base(scenario.Name)
        {
            _runner = new ScenarioRunner<T>(scenario);

            AddOption(OptionHelper.CreateOption<string>("--serviceId", isRequired: true));
            AddOption(OptionHelper.CreateOption<string>("--clusterId", isRequired: true));
            AddOption(OptionHelper.CreateOption<int>("--pipelineSize", defaultValue: 10000, validator: OptionHelper.OnlyStrictlyPositive));
            AddOption(OptionHelper.CreateOption<int>("--requests", defaultValue: 1000000, validator: OptionHelper.OnlyStrictlyPositive));
            AddOption(OptionHelper.CreateOption("--secretSource", defaultValue: SecretConfiguration.SecretSource.File));

            foreach (var opt in scenario.Options)
            {
                AddOption(opt);
            }

            Handler = CommandHandler.Create<CommonParameters, T>(_runner.Run);
        }
    }

    public static class Scenario
    {
        public static Command CreateCommand<T>(IScenario<T> scenario) => new ScenarioCommand<T>(scenario);
    }
}
