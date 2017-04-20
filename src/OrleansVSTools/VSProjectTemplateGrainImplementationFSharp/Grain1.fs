namespace $safeprojectname$

open System
open System.Threading.Tasks
open Orleans

type $safeitemname$() = 
    inherit Grain()

    interface I$safeitemname$ with   // Replace grain interface

        // Add implementations of the actual interface methods.
