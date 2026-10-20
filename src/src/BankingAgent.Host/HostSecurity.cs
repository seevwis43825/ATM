// ===== 宿主共享辅助 =====
//
// 端点拆分到独立文件后，这些原先藏在 Program.cs 顶层语句里的本地函数
// 需要有个公共的落脚点。注意本地函数不支持 XML 文档注释（会触发 CS1587），
// 所以这里用普通 XML 注释配合独立的说明性类型。

using BankingAgent.Base.Data;
using BankingAgent.Base.Security.Auth;

namespace BankingAgent.Host;

/// <summary>宿主层的安全辅助方法。</summary>
internal static class HostSecurity
{
    /// <summary>
    /// 返回 403。自定义鉴权中间件下 <c>Results.Forbid()</c> 需要 ASP.NET Core
    /// 鉴权方案配合，否则会抛 InvalidOperationException，因此显式构造响应。
    /// </summary>
    /// <param name="message">拒绝原因。</param>
    /// <returns>403 响应。</returns>
    public static IResult Forbidden(string message) => Results.Json(
        new { code = "FORBIDDEN", message },
        statusCode: StatusCodes.Status403Forbidden);

    /// <summary>免认证端点白名单。</summary>
    /// <param name="path">请求路径。</param>
    /// <returns>属于公开端点返回 true。</returns>
    public static bool IsPublic(string path) =>
        path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/api/auth/token", StringComparison.OrdinalIgnoreCase);

    /// <summary>把令牌里的角色字符串转成枚举。</summary>
    /// <param name="role">角色字符串。</param>
    /// <returns>角色枚举；未知角色回退为普通用户。</returns>
    public static ActorRole ParseRole(string role) => role switch
    {
        JwtRoles.Admin => ActorRole.Admin,
        JwtRoles.Auditor => ActorRole.Auditor,
        JwtRoles.Staff => ActorRole.Staff,
        JwtRoles.Agent => ActorRole.Agent,
        _ => ActorRole.User
    };
}
