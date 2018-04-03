Imports Interfaces
Imports Orleans.Runtime.Configuration

Module Module1

    Sub Main(args As String())

        Dim setup = New AppDomainSetup With {.AppDomainInitializer = AddressOf InitSilo, .AppDomainInitializerArguments = args}
        Dim hostDomain As AppDomain = AppDomain.CreateDomain("OrleansHost", Nothing, setup)

        Dim config As ClientConfiguration = ClientConfiguration.LocalhostSilo()
        GrainClient.Initialize(config)

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
