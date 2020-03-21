<%@ Page Title="Orleans in Azure" Language="C#" MasterPageFile="~/Site.master" AutoEventWireup="true"
    CodeBehind="Default.aspx.cs" Async="true" Inherits="Orleans.Azure.Samples.Web._Default" %>

<asp:Content ID="HeaderContent" runat="server" ContentPlaceHolderID="HeadContent">
    <title>Orleans in Azure</title>
</asp:Content>
<asp:Content ID="BodyContent" runat="server" ContentPlaceHolderID="MainContent">
    <h2>Front Deployment</h2>
    <asp:ScriptManager ID="ScriptManager1" runat="server" />
    <asp:UpdatePanel ID="UpdatePanel1" runat="server" UpdateMode="Conditional">
        <Triggers>
            <asp:AsyncPostBackTrigger ControlID="UpdateTimer" EventName="Tick" />
            <asp:PostBackTrigger ControlID="ButtonRefresh" />
        </Triggers>
        <ContentTemplate>
            <p>
                The ticker below refreshes automatically every 5 seconds.
                <asp:Button ID="ButtonRefresh" Style="margin-left: 10px; vertical-align: middle" runat="server" Text="Refresh Ticker Now" OnClick="ButtonRefresh_Click" />
            </p>
            <p>
               <asp:TextBox ID="TextBox1" runat="server" Height="39px" ReadOnly="true" TextMode="MultiLine" Width="640px">...Connecting...</asp:TextBox>
            </p>
            <p id="InputSpace">
                Enter name of event to look up: <asp:TextBox runat="server" ID="NameTextBox" Height="26px" Width="170px"></asp:TextBox>
                <asp:Button ID="ButtonLookup" runat="server" OnClick="ButtonLookup_Click" Style="margin-left: 10px; vertical-align: middle" Text="Look Up" />
            </p>
            <p>
               <asp:TextBox ID="TextBox2" runat="server" Height="174px" ReadOnly="true" TextMode="MultiLine" Width="640px"></asp:TextBox>
            </p>
            <asp:Timer ID="UpdateTimer" runat="server" Interval="5000" OnTick="Timer1_Tick" />
         </ContentTemplate>
    </asp:UpdatePanel>
    <asp:UpdateProgress ID="UpdateProgress1" runat="server" AssociatedUpdatePanelID="UpdatePanel1">
        <ProgressTemplate>
            <asp:Label ID="ProgressStatusLabel" runat="server" Text="Talking to Orleans...."></asp:Label>
        </ProgressTemplate>
    </asp:UpdateProgress>
</asp:Content>
