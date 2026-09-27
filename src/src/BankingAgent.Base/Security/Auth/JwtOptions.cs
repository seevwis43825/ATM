// ===== 身份认证（JWT）=====
// 修复原实现的越权漏洞：userId 由客户端传入，任何人可传别人的 userId
// 查询他人卡、账单、转账。现在 userId 一律从已验证的 Token 中提取。

namespace BankingAgent.Base.Security.Auth;

using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

/// <summary>JWT 配置。生产环境密钥必须来自环境变量或密钥管理服务。</summary>
public sealed class JwtOptions
{
    /// <summary>签名密钥。长度必须 ≥ 32 字节（HS256 要求）。</summary>
    public string SigningKey { get; set; } = "dev-only-signing-key-change-me-in-production!!";
    /// <summary>签发者。</summary>
    public string Issuer { get; set; } = "banking-agent";
    /// <summary>受众。</summary>
    public string Audience { get; set; } = "banking-agent-client";
    /// <summary>访问令牌有效期（分钟）。</summary>
    public int ExpirationMinutes { get; set; } = 30;

    /// <summary>判断当前是否为开发模式（允许默认密钥）。</summary>
    public bool AllowDevelopmentKey =>
        SigningKey.StartsWith("dev-only-", StringComparison.Ordinal);
}

/// <summary>令牌中的角色。与文档 05-security-compliance/02 的角色矩阵一致。</summary>
public static class JwtRoles
{
    public const string User = "user";
    public const string Agent = "agent";
    public const string Staff = "staff";
    public const string Auditor = "auditor";
    public const string Admin = "admin";
}

/// <summary>当前请求的操作者身份。由认证中间件填充，取代客户端传入的 userId。</summary>
public sealed class CurrentPrincipal
{
    public string UserId { get; init; } = "";
    public string Role { get; init; } = JwtRoles.User;
    public string DisplayName { get; init; } = "";
    public bool IsAuthenticated { get; init; }

    /// <summary>是否具备审计只读权限。</summary>
    public bool CanAudit => Role is JwtRoles.Auditor or JwtRoles.Admin;

    /// <summary>是否具备管理权限。</summary>
    public bool CanAdminister => Role == JwtRoles.Admin;

    /// <summary>是否可接管用户对话（客服场景）。</summary>
    public bool CanImpersonateSupport => Role is JwtRoles.Staff or JwtRoles.Auditor or JwtRoles.Admin;
}

/// <summary>令牌签发与验证契约。</summary>
public interface ITokenService
{
    /// <summary>签发访问令牌。</summary>
    string IssueToken(string userId, string role = JwtRoles.User, string? displayName = null);

    /// <summary>验证令牌并提取身份。失败返回未认证主体。</summary>
    CurrentPrincipal Validate(string? token);
}
