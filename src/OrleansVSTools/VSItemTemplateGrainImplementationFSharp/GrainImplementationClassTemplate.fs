namespace $rootnamespace$

open System
open System.Threading.Tasks
open Orleans

type $safeitemname$() = 
    inherit Orleans.Grain()

    interface I$safeitemname$ with   // Replace grain interface

        // Add implementations of the actual interface methods.
