﻿/*
Project Orleans Cloud Service SDK ver. 1.0

Copyright (c) Microsoft Corporation

All rights reserved.

MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace OrleansManager
{
    [RunInstaller(true)]
    public class OrleansManagerSnapIn : CustomPSSnapIn
    {
        public override string Name => "Orleans.Manager";

        public override string Vendor => "Microsoft Corporation";

        public override string Description => "Cmdlets to manage Orleans instances.";

        public override Collection<CmdletConfigurationEntry> Cmdlets => new Collection<CmdletConfigurationEntry>
        {
            new CmdletConfigurationEntry("Get-GrainStatistics", typeof (GetGrainStatistics), null),
            new CmdletConfigurationEntry("Collect-Activations", typeof(CollectActivations), null), 
            new CmdletConfigurationEntry("Remove-Grain", typeof(UnregisterGrain), null),
            new CmdletConfigurationEntry("Find-Grain", typeof(LookupGrain), null),
            new CmdletConfigurationEntry("Get-GrainReport", typeof(GetGrainReport), null),
            /* register other cmdlets or alias here */
        };
    }
}
