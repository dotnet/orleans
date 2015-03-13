'*********************************************************
'    Copyright (c) Microsoft. All rights reserved.
'    
'    Apache 2.0 License
'    
'    You may obtain a copy of the License at
'    http://www.apache.org/licenses/LICENSE-2.0
'    
'    Unless required by applicable law or agreed to in writing, software 
'    distributed under the License is distributed on an "AS IS" BASIS, 
'    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or 
'    implied. See the License for the specific language governing 
'    permissions and limitations under the License.
'
'*********************************************************

Imports Interfaces

Module Module1

    Sub Main(args As String())

        Dim setup = New AppDomainSetup With {.AppDomainInitializer = AddressOf InitSilo, .AppDomainInitializerArguments = args}
        Dim hostDomain As AppDomain = AppDomain.CreateDomain("OrleansHost", Nothing, setup)

        GrainClient.Initialize("DevTestClientConfiguration.xml")

        Dim grain = GrainFactory.GetGrain(Of IHello)(0)

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
