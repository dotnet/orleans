using Orleans;
using System;
using System.Collections.Generic;
using System.Management.Automation;
using System.Reflection;

namespace OrleansPSUtils
{
    [Cmdlet(VerbsCommon.Get, "Grain", DefaultParameterSetName = StringKeySet)]
    public class GetGrain : PSCmdlet
    {
        private const string GuidKeySet = "GuidKey";
        private const string LongKeySet = "LongKey";
        private const string StringKeySet = "StringKey";
        private const string GuidCompoundKeySet = "GuidCompoundKey";
        private const string LongCompoundKeySet = "LongCompoundKey";

        [Parameter(Position = 1, Mandatory = true, ValueFromPipeline = true, ParameterSetName = GuidKeySet)]
        [Parameter(Position = 1, Mandatory = true, ValueFromPipeline = true, ParameterSetName = LongKeySet)]
        [Parameter(Position = 1, Mandatory = true, ValueFromPipeline = true, ParameterSetName = StringKeySet)]
        [Parameter(Position = 1, Mandatory = true, ValueFromPipeline = true, ParameterSetName = GuidCompoundKeySet)]
        [Parameter(Position = 1, Mandatory = true, ValueFromPipeline = true, ParameterSetName = LongCompoundKeySet)]
        public Type GrainType { get; set; }

        [Parameter(Position = 2, Mandatory = true, ValueFromPipeline = true, ParameterSetName = StringKeySet)]
        public string StringKey { get; set; }

        [Parameter(Position = 3, Mandatory = true, ValueFromPipeline = true, ParameterSetName = GuidKeySet)]
        [Parameter(Position = 3, Mandatory = true, ValueFromPipeline = true, ParameterSetName = GuidCompoundKeySet)]
        public Guid GuidKey { get; set; }

        [Parameter(Position = 4, Mandatory = true, ValueFromPipeline = true, ParameterSetName = LongKeySet)]
        [Parameter(Position = 4, Mandatory = true, ValueFromPipeline = true, ParameterSetName = LongCompoundKeySet)]
        public long LongKey { get; set; }

        [Parameter(Position = 5, ValueFromPipeline = true, ParameterSetName = StringKeySet)]
        [Parameter(Position = 5, ValueFromPipeline = true, ParameterSetName = GuidKeySet)]
        [Parameter(Position = 5, ValueFromPipeline = true, ParameterSetName = LongKeySet)]
        [Parameter(Position = 5, ValueFromPipeline = true, ParameterSetName = GuidCompoundKeySet)]
        [Parameter(Position = 5, ValueFromPipeline = true, ParameterSetName = LongCompoundKeySet)]
        public string GrainClassNamePrefix { get; set; } = null;

        [Parameter(Position = 6, Mandatory = true, ValueFromPipeline = true, ParameterSetName = LongCompoundKeySet)]
        [Parameter(Position = 6, Mandatory = true, ValueFromPipeline = true, ParameterSetName = GuidCompoundKeySet)]
        public string KeyExtension { get; set; }

        protected override void ProcessRecord()
        {
            try
            {
                if (!GrainClient.IsInitialized)
                    throw new InvalidOperationException("GrainClient not initialized. Call 'Start-GrainClient' before call Get-Grain");

                MethodInfo baseMethodInfo = null;
                var methodName = "GetGrain";
                var methodParams = new List<object>();

                switch (ParameterSetName)
                {
                    case GuidKeySet:
                        baseMethodInfo = GrainClient.GrainFactory.GetType().GetMethod(methodName, new[] { typeof(Guid), typeof(string) });
                        methodParams.Add(GuidKey);
                        break;
                    case GuidCompoundKeySet:
                        baseMethodInfo = GrainClient.GrainFactory.GetType().GetMethod("GetGrain", new[] { typeof(Guid), typeof(string), typeof(string) });
                        methodParams.Add(GuidKey);
                        methodParams.Add(KeyExtension);
                        break;
                    case LongKeySet:
                        baseMethodInfo = GrainClient.GrainFactory.GetType().GetMethod(methodName, new[] { typeof(long), typeof(string) });
                        methodParams.Add(LongKey);
                        break;
                    case LongCompoundKeySet:
                        baseMethodInfo = GrainClient.GrainFactory.GetType().GetMethod("GetGrain", new[] { typeof(long), typeof(string), typeof(string) });
                        methodParams.Add(LongKey);
                        methodParams.Add(KeyExtension);
                        break;
                    case StringKeySet:
                        baseMethodInfo = GrainClient.GrainFactory.GetType().GetMethod(methodName, new[] { typeof(string), typeof(string) });
                        methodParams.Add(StringKey);
                        break;
                }

                methodParams.Add(GrainClassNamePrefix);

                var getGrainMethod = baseMethodInfo.MakeGenericMethod(GrainType);
                var grain = getGrainMethod.Invoke(GrainClient.GrainFactory, methodParams.ToArray());

                WriteObject(grain);
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, ex.GetType().Name, ErrorCategory.InvalidOperation, this));
            }
        }
    }
}
