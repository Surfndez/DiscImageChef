﻿<%@ Page Language="C#" %>
<!DOCTYPE html>
<html>
<meta charset="UTF-8">
<head runat="server">
    <link type='text/css' rel='stylesheet' href='dos.css' />
    <title>DiscImageChef's TODO</title>
</head>
<body id="body" runat="server">
    <p>
        <a href="Default.aspx">Return to main page.</a><br/>
        DiscImageChef list of changes:
    </p>
    <div>
        <velyo:MarkdownContent ID="todo" runat="server" Path="~/docs/Changelog.md" />
    </div>
</body>
</html>