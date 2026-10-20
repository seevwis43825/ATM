// ===== 认证端点 =====
//
// 演示环境的账号校验。生产环境必须接银行统一身份认证 + OTP/MFA，
// 口令不得以明文或可逆方式存储 —— 这里的明文口令表只用于本地演示。

using BankingAgent.Base.Security.Auth;
using BankingAgent.Host.Contracts;

namespace BankingAgent.Host.Endpoints;

/// <summary>令牌签发与身份查询端点。</summary>
public static class AuthEndpoints
{
    /// <summary>注册认证端点。</summary>
    /// <param name="app">端点路由构建器。</param>
    /// <returns>路由构建器。</returns>
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/token", (TokenRequest req, ITokenService tokenService) =>
                IssueToken(req, tokenService))
            .WithTags("认证")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized);

        app.MapGet("/api/auth/me", (HttpContext ctx) => DescribeCurrentUser(ctx))
            .WithTags("认证")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }

    /// <summary>POST /api/auth/token — 签发令牌。</summary>
    /// <param name="req">令牌请求。</param>
    /// <param name="tokenService">令牌服务。</param>
    /// <returns>令牌；参数非法返回 400，账号或口令错误统一返回 401。</returns>
    private static IResult IssueToken(TokenRequest req, ITokenService tokenService)
    {
        if (string.IsNullOrWhiteSpace(req.UserId) || string.IsNullOrWhiteSpace(req.Password))
        {
            return Results.BadRequest(new { code = "INVALID_REQUEST", message = "用户名与密码不能为空" });
        }

        if (!DemoCredentials.TryValidate(req.UserId, req.Password, out var displayName))
        {
            // 统一返回 401，不区分"用户不存在"与"密码错误"，避免账号枚举
            return Results.Unauthorized();
        }

        // 角色一律以服务端账号表为准，绝不信任客户端传入的角色（否则可自助提权）
        var role = DemoCredentials.GetRole(req.UserId) ?? JwtRoles.User;
        var token = tokenService.IssueToken(req.UserId, role, displayName);

        return Results.Ok(new
        {
            token,
            tokenType = "Bearer",
            userId = req.UserId,
            role,
            displayName,
            expiresInSeconds = 1800
        });
    }

    /// <summary>GET /api/auth/me — 查询当前令牌对应的身份。</summary>
    /// <param name="ctx">当前请求上下文。</param>
    /// <returns>身份信息。</returns>
    private static IResult DescribeCurrentUser(HttpContext ctx)
    {
        var principal = (CurrentPrincipal?)ctx.Items["Principal"];
        if (principal is null) return Results.Unauthorized();

        return Results.Ok(new
        {
            userId = principal.UserId,
            role = principal.Role,
            displayName = principal.DisplayName,
            canAudit = principal.CanAudit,
            canAdminister = principal.CanAdminister
        });
    }
}

/// <summary>
/// 演示环境的账号表。生产环境必须替换为银行统一身份认证，
/// 密码不得以明文或可逆方式存储，且需支持 OTP / MFA。
/// </summary>
internal static class DemoCredentials
{
    private static readonly Dictionary<string, (string Password, string Name, string Role)> Accounts = new()
    {
        ["u_demo01"] = ("demo1234", "张明", JwtRoles.User),
        ["u_demo02"] = ("demo1234", "李华", JwtRoles.User),
        ["u_demo03"] = ("demo1234", "王芳", JwtRoles.User),
        ["staff_01"] = ("staff1234", "客服小李", JwtRoles.Staff),
        ["audit_01"] = ("audit1234", "审计员小王", JwtRoles.Auditor),
        ["admin_01"] = ("admin1234", "管理员", JwtRoles.Admin)
    };

    /// <summary>校验演示账号。</summary>
    /// <param name="userId">账号。</param>
    /// <param name="password">口令。</param>
    /// <param name="displayName">校验通过时输出显示名。</param>
    /// <returns>校验通过返回 true。</returns>
    public static bool TryValidate(string userId, string password, out string displayName)
    {
        displayName = "";
        if (!Accounts.TryGetValue(userId, out var account)) return false;
        if (account.Password != password) return false;
        displayName = account.Name;
        return true;
    }

    /// <summary>查询账号角色，供签发令牌时使用。</summary>
    /// <param name="userId">账号。</param>
    /// <returns>角色字符串；账号不存在时返回 null。</returns>
    public static string? GetRole(string userId) =>
        Accounts.TryGetValue(userId, out var a) ? a.Role : null;
}
