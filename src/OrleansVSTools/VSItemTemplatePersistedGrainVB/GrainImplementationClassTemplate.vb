Imports Orleans

Namespace $rootnamespace$

    ''' <summary>
    ''' Orleans grain implementation class $safeitemname$
    ''' </summary>
    <StorageProvider(ProviderName:="TODO: name storage provider")>
    Public Class $safeitemname$
        Inherits Grain(Of I$safeitemname$State)
        Implements I$safeitemname$

        ' TODO: replace placeholder grain interface with actual 
        '       grain communication interface(s).
        ' Also, name the intended storage provider in the attribute above.
        '
        ' The persisted grain state is available via the 'State' property.
        ' Your logic should make sure to save the persisted state to storage
        ' using 'State.WriteStateAsync(),' which is an asynchronous operation.

    End Class

    Public Interface I$safeitemname$State
        ' TODO: add a property for each item of the persisted grain state
    End Interface

End Namespace