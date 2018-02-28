Imports Orleans.Runtime.Configuration
Imports Orleans.Runtime.Host

Friend Class OrleansHostWrapper
    Implements IDisposable

    Public Sub New(args As String())
        ParseArguments(args)
        Init()
    End Sub

    Public Function Run() As Boolean

        Dim ok = False

        Try
            siloHost.InitializeOrleansSilo()

            ok = siloHost.StartOrleansSilo()

            If ok Then
                Console.WriteLine(String.Format("Successfully started Orleans silo '{0}' as a {1} node.", siloHost.Name, siloHost.Type))
            Else
                Throw New SystemException(String.Format("Failed to start Orleans silo '{0}' as a {1} node.", siloHost.Name, siloHost.Type))
            End If
        Catch exc As Exception
            siloHost.ReportStartupError(exc)
            Dim msg = String.Format("{0}:\n{1}\n{2}", exc.GetType().FullName, exc.Message, exc.StackTrace)
            Console.WriteLine(msg)
        End Try

        Return ok

    End Function

    Public Function StopRunning() As Boolean

        Dim ok = False

        Try
            siloHost.StopOrleansSilo()

            Console.WriteLine(String.Format("Orleans silo '{0}' shutdown.", siloHost.Name))

        Catch exc As Exception
            siloHost.ReportStartupError(exc)
            Dim msg = String.Format("{0}:\n{1}\n{2}", exc.GetType().FullName, exc.Message, exc.StackTrace)
            Console.WriteLine(msg)
        End Try

        Return ok

    End Function

    Private Sub Init()
        siloHost.LoadOrleansConfig()
    End Sub

    Private Function ParseArguments(args As String()) As Boolean
        Dim deploymentId As String = Nothing
        Dim siloName = System.Net.Dns.GetHostName() ' Default to machine name
        Dim config As ClusterConfiguration
        Dim argPos = 1

        For i As Integer = 0 To args.Length - 1
            Dim a = args(i)
            If (a.StartsWith("-") OrElse a.StartsWith("/")) Then
                Select Case a.ToLowerInvariant()
                    Case "/?"
                    Case "/help"
                    Case "-?"
                    Case "-help"
                        Return False
                    Case Else
                        Console.WriteLine("Bad command line argument supplied: " & a)
                        Return False
                End Select
            ElseIf a.Contains("=") Then

                Dim split = a.Split("=")

                If String.IsNullOrEmpty(split(1)) Then
                    Console.WriteLine("Bad command line argument supplied: " & a)
                    Return False
                End If

                Select Case split(0).ToLowerInvariant()
                    Case "deploymentid"
                        deploymentId = split(1)
                    Case Else
                        Console.WriteLine("Bad command line argument supplied: " & a)
                        Return False
                End Select
                ' Process unqualified arguments
            ElseIf argPos = 1 Then
                siloName = a
                argPos += 1
            Else
                ' Too many command line arguments
                Console.WriteLine("Too many command line arguments supplied: " + a)
                Return False
            End If
        Next

        config = ClusterConfiguration.LocalhostPrimarySilo()
        siloHost = New SiloHost(siloName, config)

        If deploymentId IsNot Nothing Then
            siloHost.DeploymentId = deploymentId
        End If

        Return True
    End Function


    Private siloHost As SiloHost

#Region "IDisposable Support"
    Private disposedValue As Boolean

    ' IDisposable
    Protected Overridable Sub Dispose(disposing As Boolean)
        If Not Me.disposedValue AndAlso siloHost IsNot Nothing Then
            siloHost.Dispose()
        End If
        Me.disposedValue = True
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        ' Do not change this code.  Put cleanup code in Dispose(disposing As Boolean) above.
        Dispose(True)
        GC.SuppressFinalize(Me)
    End Sub
#End Region

End Class
