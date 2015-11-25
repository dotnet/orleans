(*
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
*)

namespace UnitTests.FSharpTypes

open System
open Orleans.Concurrency

[<Serializable; Immutable>]
type SingleCaseDU = 
    private 
    | SingleCaseDU of int
    static member ofInt i = SingleCaseDU i

[<Serializable; Immutable>]
type Record = { A: SingleCaseDU } with
    static member ofInt x = { A = SingleCaseDU.ofInt x }

[<Serializable; Immutable>]
type RecordOfIntOption = { A: int option } with
    static member Empty = { A = None }
    static member ofInt x = { A = Some x}

type RecordOfIntOptionWithNoAttributes = { A: int option } with
    static member Empty = { A = None }
    static member ofInt x = { A = Some x}

[<Serializable; Immutable>]
type GenericRecord<'T> = { Value: 'T } with
    static member ofT x = { Value = x }
