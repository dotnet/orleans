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

        private const string GetGrainMethodName = "GetGrain";

        [Parameter(Mandatory = false, ValueFromPipelineByPropertyName = true, ParameterSetName = GuidKeySet)]
        [Parameter(Mandatory = false, ValueFromPipelineByPropertyName = true, ParameterSetName = LongKeySet)]
        [Parameter(Mandatory = false, ValueFromPipelineByPropertyName = true, ParameterSetName = StringKeySet)]
        [Parameter(Mandatory = false, ValueFromPipelineByPropertyName = true, ParameterSetName = GuidCompoundKeySet)]
        [Parameter(Mandatory = false, ValueFromPipelineByPropertyName = true, ParameterSetName = LongCompoundKeySet)]
        public IClusterClient Client { get; set; }

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
                var client = this.Client ?? this.GetClient();
                if (client == null)
                    throw new InvalidOperationException("Client not initialized. Call 'Start-GrainClient' before call Get-Grain");

                MethodInfo baseMethodInfo = null;
                var methodParams = new List<object>();

                switch (this.ParameterSetName)
                {
                    case GuidKeySet:
                        baseMethodInfo = client.GetType()
                            .GetMethod(GetGrainMethodName, new[] { typeof(Guid), typeof(string) });
                        methodParams.Add(this.GuidKey);
                        break;
                    case GuidCompoundKeySet:
                        baseMethodInfo = client.GetType()
                            .GetMethod(GetGrainMethodName, new[] { typeof(Guid), typeof(string), typeof(string) });
                        methodParams.Add(this.GuidKey);
                        methodParams.Add(this.KeyExtension);
                        break;
                    case LongKeySet:
                        baseMethodInfo = client.GetType()
                            .GetMethod(GetGrainMethodName, new[] { typeof(long), typeof(string) });
                        methodParams.Add(this.LongKey);
                        break;
                    case LongCompoundKeySet:
                        baseMethodInfo = client.GetType()
                            .GetMethod(GetGrainMethodName, new[] { typeof(long), typeof(string), typeof(string) });
                        methodParams.Add(this.LongKey);
                        methodParams.Add(this.KeyExtension);
                        break;
                    case StringKeySet:
                        baseMethodInfo = client.GetType()
                            .GetMethod(GetGrainMethodName, new[] { typeof(string), typeof(string) });
                        methodParams.Add(this.StringKey);
                        break;
                }

                methodParams.Add(this.GrainClassNamePrefix);

                var getGrainMethod = baseMethodInfo.MakeGenericMethod(this.GrainType);
                var grain = getGrainMethod.Invoke(client, methodParams.ToArray());

                this.WriteObject(grain);
            }
            catch (Exception ex)
            {
                this.WriteError(new ErrorRecord(ex, ex.GetType().Name, ErrorCategory.InvalidOperation, this));
            }
        }
    }
}
