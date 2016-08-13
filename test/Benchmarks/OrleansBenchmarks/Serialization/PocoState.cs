using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;

namespace SerializationBenchmarks
{
    [Serializable]
    public class PocoState
    {
        public string Str { get; set; }

        public int Num1 { get; set; }

        public double Num2 { get; set; }

        public decimal Num3 { get; set; }
    }

}
