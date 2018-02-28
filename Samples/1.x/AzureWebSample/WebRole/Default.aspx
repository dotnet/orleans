<%@ Page Title="Orleans in Azure" Language="C#" MasterPageFile="~/Site.master" AutoEventWireup="true"
    CodeBehind="Default.aspx.cs" Async="true" Inherits="Orleans.Azure.Samples.Web._Default" %>

<asp:Content ID="HeaderContent" runat="server" ContentPlaceHolderID="HeadContent">
    <title>Orleans in Azure</title>
</asp:Content>
<asp:Content ID="BodyContent" runat="server" ContentPlaceHolderID="MainContent">
    <h2>
        Welcome to Orleans running in Azure!
    </h2>
    <asp:ScriptManager ID="ScriptManager1" runat="server"  />
    <asp:UpdatePanel ID="UpdatePanel1" runat="server">
        <ContentTemplate>
            <p id="InputSpace">
                <asp:Button ID="ButtonSayHello" runat="server" Text="Ask Orleans its details" 
                    onclick="ButtonSayHello_Click" />
            </p>
            <p id="ReplySpace">
                <asp:TextBox ID="ReplyText" runat="server" ReadOnly="true" Width="100%" 
                    Height="200px" TextMode="MultiLine" >Hoping I will get a reply message from Orleans...</asp:TextBox>
            </p>
        </ContentTemplate>
    </asp:UpdatePanel>
    <asp:UpdateProgress ID="UpdateProgress1" runat="server" AssociatedUpdatePanelID="UpdatePanel1">
        <ProgressTemplate>
            <asp:Label id="ProgressStatusLabel" runat="server" Text="Talking to Orleans...."></asp:Label> 
        </ProgressTemplate>
    </asp:UpdateProgress>
</asp:Content>
