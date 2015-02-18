using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Orleans;

namespace FxCopTestGrains
{
    public interface IGrain1 : Orleans.IGrain
    {
        AsyncValue<string> SayHello(string name);
        AsyncValue<string> SayGoodbye(string name);
        AsyncValue<string> SayWhat(string why);
        AsyncValue<string> SayHey(string ho);
    }
}
