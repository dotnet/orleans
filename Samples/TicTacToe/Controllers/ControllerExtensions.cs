using System;
using Microsoft.AspNetCore.Mvc;

namespace TicTacToe.Controllers
{
    public static class ControllerExtensions
    {
        public static Guid GetGuid(this ControllerBase controller)
        {
            if (controller.Request.Cookies["playerId"] is { Length: > 0 } idCookie)
            {
                return Guid.Parse(idCookie);
            }

            var guid = Guid.NewGuid();
            controller.Response.Cookies.Append("playerId", guid.ToString());
            return guid;
        }
    }
}
