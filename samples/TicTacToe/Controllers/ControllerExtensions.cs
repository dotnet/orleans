using Microsoft.AspNetCore.Mvc;

namespace TicTacToe.Controllers;

public static class ControllerExtensions
{
    public static Guid GetGuid(this ControllerBase controller)
    {
        if (Guid.TryParse(controller.Request.Cookies["playerId"], out var id))
        {
            return id;
        }

        var guid = Guid.NewGuid();
        controller.Response.Cookies.Append(
            "playerId",
            guid.ToString(),
            new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = true
            });
        return guid;
    }
}
