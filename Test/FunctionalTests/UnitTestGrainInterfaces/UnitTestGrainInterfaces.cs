using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;


namespace UnitTestGrains
{
    public interface IA : IGrain
    {
        Task<string> A1Method();
        Task<string> A2Method();
        Task<string> A3Method();
    }

    public interface IB : IGrain
    {
        Task<string> B1Method();
        Task<string> B2Method();
        Task<string> B3Method();
    }

    public interface IC : IA, IB
    {
        Task<string> C1Method();
        Task<string> C2Method();
        Task<string> C3Method();
    }

    public interface ID : IC
    {
        Task<string> D1Method();
        Task<string> D2Method();
        Task<string> D3Method();
    }

    public interface IE : IGrain
    {
        Task<string> E1Method();
        Task<string> E2Method();
        Task<string> E3Method();
    }

    public interface IF : ID, IE
    {
        Task<string> F1Method();
        Task<string> F2Method();
        Task<string> F3Method();
    }

    public interface IG : IGrain
    {
        Task<string> AmbiguousMethod();
    }
    public interface IH : IGrain
    {
        Task<string> H1Method();
        Task<string> H2Method();
        Task<string> H3Method();
    }

    public interface IServiceType : IF
    {
        Task<string> ServiceTypeMethod1();
        Task<string> ServiceTypeMethod2();
        Task<string> ServiceTypeMethod3();
    }

    public interface IDerivedServiceType : IServiceType, IH
    {
        Task<string> DerivedServiceTypeMethod1();
    }
}
