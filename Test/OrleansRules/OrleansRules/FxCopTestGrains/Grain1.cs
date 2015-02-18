using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Orleans;

namespace FxCopTestGrains
{
    /// <summary>
    /// Orleans grain implementation class.
    /// </summary>
    [Serializable]
    public abstract class Grain1 : Orleans.Grain, IGrain1
    {
        public AsyncValue<string> SayHello(string name)
        {
            return "Hello " + name;
        }

        public AsyncValue<string> SayGoodbye(string name)
        {
            return "Hello " + name;
        }

        public AsyncValue<string> SayWhat(string why)
        {
            return "What is " + why;
        }

        public AsyncValue<string> SayHey(string ho)
        {
            return "Hey " + ho;
        }
    }
}
