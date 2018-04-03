Imports Orleans

''' <summary>
''' Orleans grain implementation class Grain1
''' </summary>
Public Class Grain1
    Inherits Grain
    Implements Interfaces.IHello

    Public Function SayHello(greeting As String) As Task(Of String) Implements Interfaces.IHello.SayHello
        Return Task.FromResult("Hello, this is Visual Basic speaking!")
    End Function

End Class
