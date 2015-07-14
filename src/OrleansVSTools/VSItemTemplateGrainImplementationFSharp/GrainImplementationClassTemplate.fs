namespace $rootnamespace$

open System
open System.Threading.Tasks
open Orleans

type $safeitemname$() = 
    inherit Orleans.GrainBase()

    interface I$safeitemname$ with   // Replace grain interface

        // Add implementations of the actual interface methods.
