<%@ Page Title="About Us" Language="C#" MasterPageFile="~/Site.master" AutoEventWireup="true"
    CodeBehind="About.aspx.cs" Inherits="Orleans.Azure.Samples.Web.About" %>

<asp:Content ID="HeaderContent" runat="server" ContentPlaceHolderID="HeadContent">
    <title>About - Orleans in Azure</title>
</asp:Content>
<asp:Content ID="BodyContent" runat="server" ContentPlaceHolderID="MainContent">
    <h2>
        About - Orleans running in Azure
    </h2>
    <p>
        OrleansAzureUtils v<%= Orleans.Host.Azure.Utils.OrleansAzureConstants.Version %>
    </p>
</asp:Content>
