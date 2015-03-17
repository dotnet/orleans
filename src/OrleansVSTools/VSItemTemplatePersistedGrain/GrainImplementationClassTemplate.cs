/*
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

﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using Orleans;
﻿using Orleans.Providers;

namespace $rootnamespace$
{
    /// <summary>
    /// Orleans grain implementation class $safeitemname$
    /// </summary>
    [StorageProvider(ProviderName = "TODO: name storage provider")]
    public class $safeitemname$ : Grain<I$safeitemname$State>, $rootnamespace$.I$safeitemname$
	{
        // TODO: replace placeholder grain interface with actual grain
        // communication interface(s).
        //
        // Also, name the intended storage provider in the attribute above.
        //
        // The persisted grain state is available via the 'State' property.
        // Your logic should make sure to save the persisted state to storage
        // using 'State.WriteStateAsync(),' which is an asynchronous operation.
    }

    public interface I$safeitemname$State : IGrainState
    {
        // TODO: add a property for each item of the persisted grain state
    }
}
