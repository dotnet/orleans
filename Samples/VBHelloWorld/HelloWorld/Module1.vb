'*********************************************************
'    Project Orleans Cloud Service SDK ver. 1.0
' 
'    Copyright (c) Microsoft Corporation
' 
'    All rights reserved.
' 
'    MIT License
'
'    Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
'    associated documentation files (the ""Software""), to deal in the Software without restriction,
'    including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
'    and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
'    subject to the following conditions:
'
'    The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
'
'    THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
'    THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
'    OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
'    TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
'*********************************************************

Imports Interfaces

Module Module1

    Sub Main(args As String())

        Dim setup = New AppDomainSetup With {.AppDomainInitializer = AddressOf InitSilo, .AppDomainInitializerArguments = args}
        Dim hostDomain As AppDomain = AppDomain.CreateDomain("OrleansHost", Nothing, setup)

        GrainClient.Initialize("DevTestClientConfiguration.xml")

        Dim grain = GrainClient.GrainFactory.GetGrain(Of IHello)(0)

        Console.WriteLine(vbNewLine & vbNewLine & "{0}" & vbNewLine & vbNewLine, grain.SayHello("Good morning!").Result)

        Console.WriteLine("Orleans Silo is running.\nPress Enter to terminate...")
        Console.ReadLine()

        hostDomain.DoCallBack(AddressOf ShutdownSilo)

    End Sub

    Sub InitSilo(args As String())
        hostWrapper = New OrleansHostWrapper(args)

        If Not hostWrapper.Run() Then
            Console.Error.WriteLine("Failed to initialize Orleans silo")
        End If
    End Sub

    Sub ShutdownSilo()

        If hostWrapper IsNot Nothing Then
            hostWrapper.Dispose()
        End If
    End Sub

    Private hostWrapper As OrleansHostWrapper
End Module
