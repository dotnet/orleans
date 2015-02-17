using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;

namespace MultifacetGrain
{
    //public delegate void ValueUpdateEventHandler(object sender, ValueUpdateEventArgs e);

    //[Serializable]
    //public class ValueUpdateEventArgs : EventArgs
    //{
    //    public int Value { get; private set; }

    //    public ValueUpdateEventArgs(int x)
    //    {
    //        Value = x;
    //    }
    //}

    public interface IMultifacetReader : IGrain
    {
        Task<int> GetValue();
        //event ValueUpdateEventHandler ValueUpdateEvent;
        //event ValueUpdateEventHandler CommonEvent;
    }

    public interface IMultifacetWriter : IGrain
    {
        Task SetValue(int x);
        //event ValueUpdateEventHandler ValueReadEvent;
        //event ValueUpdateEventHandler CommonEvent;
    }

    public interface IMultifacetTestGrain : IMultifacetReader, IMultifacetWriter
    {
    }
}
