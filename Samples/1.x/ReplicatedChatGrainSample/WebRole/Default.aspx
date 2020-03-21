<%@ Page Title="Orleans in Azure" Language="C#" MasterPageFile="~/Site.master" AutoEventWireup="true"
    CodeBehind="Default.aspx.cs" Async="true" Inherits="Orleans.Azure.Samples.Web._Default" %>

<asp:Content ID="HeaderContent" runat="server" ContentPlaceHolderID="HeadContent">
    <title>Orleans in Azure</title>
</asp:Content>
<asp:Content ID="BodyContent" runat="server" ContentPlaceHolderID="MainContent">
    <h2>Welcome to Orleans running in Azure!
    </h2>
    <asp:ScriptManager ID="ScriptManager1" runat="server" />
    <asp:UpdatePanel ID="UpdatePanel1" runat="server" UpdateMode="Conditional">
        <Triggers>
            <asp:AsyncPostBackTrigger ControlID="UpdateTimer" EventName="Tick" />
            <asp:PostBackTrigger ControlID="ButtonRefresh" />
        </Triggers>
        <ContentTemplate>
            <p id="InputSpace">
                <p>The ChatGrain contains a list of messages. There are two kinds of events: AppendMessage and ClearAllMessages.</p>
                <asp:TextBox runat="server" ID="NameTextBox"></asp:TextBox>
                <asp:Button ID="ButtonAppendMessage" runat="server" Text="AppendMessage" OnClick="ButtonAppendMessage_Click" />
                <asp:Button ID="ButtonClearAll" runat="server" Style="margin-left: 40px" Text="ClearAllMessages" OnClick="ButtonClearAll_Click" />
           <asp:Timer ID="UpdateTimer" runat="server" Interval="5000" OnTick="Timer1_Tick" />
            <table>
                <tr>
                    <td style="vertical-align: top">
                        <p>
                            Tentative State (= Last Confirmed + Queue) <p>
                                <asp:TextBox ID="TentativeState" runat="server" Height="393px" ReadOnly="true" TextMode="MultiLine" Width="340px">...Connecting...</asp:TextBox>
                    </td>
                    <td>
                               <p>
                                    Queue of Unconfirmed Events<p>
                                        <asp:TextBox ID="UnconfirmedUpdates" runat="server" Height="110px" ReadOnly="true" TextMode="MultiLine" Width="340px">...Connecting...</asp:TextBox>
                        <p>
                            Last Confirmed state<p>
                                <asp:TextBox ID="ConfirmedState" runat="server" Height="230px" ReadOnly="true" TextMode="MultiLine" Width="340px">...Connecting...</asp:TextBox>
                     </td>
                </tr>
            </table>
                <p>
                    This page refreshes automatically every 5 seconds, to display the current local grain state.
                <asp:Button ID="ButtonRefresh" Style="margin-left: 10px; vertical-align: middle" runat="server" Text="Refresh Now" OnClick="ButtonRefresh_Click" />
                </p>
        </ContentTemplate>
    </asp:UpdatePanel>
    <asp:UpdateProgress ID="UpdateProgress1" runat="server" AssociatedUpdatePanelID="UpdatePanel1">
        <ProgressTemplate>
            <asp:Label ID="ProgressStatusLabel" runat="server" Text="Talking to Orleans...."></asp:Label>
        </ProgressTemplate>
    </asp:UpdateProgress>
</asp:Content>
