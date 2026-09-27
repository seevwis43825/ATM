// ===== JWT 令牌服务 =====
// 采用对称 HS256 签名，令牌自包含，适合单体架构与快速落地。
// 多实例部署或需第三方接入时，应改用非对称 RS256 + JWKS 端点。

namespace BankingAgent.Base.Security.Auth;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

/// <summary>基于 HS256 的令牌服务。</summary>
public sealed class TokenService : ITokenService
{
    private readonly JwtOptions _options;
    private readonly ILogger<TokenService> _logger;
    private readonly SymmetricSecurityKey _key;

    /// <summary>构造令牌服务并校验密钥强度。</summary>
    public TokenService(IOptions<JwtOptions> options, ILogger<TokenService> logger)
    {
        _options = options.Value;
        _logger = logger;

        if (Encoding.UTF8.GetByteCount(_options.SigningKey) < 32)
        {
            throw new InvalidOperationException(
                "JWT 签名密钥长度不足 32 字节，无法用于 HS256。请配置 Jwt:SigningKey。");
        }

        if (_options.AllowDevelopmentKey)
        {
            _logger.LogWarning(
                "警告：正在使用开发默认 JWT 密钥，生产环境必须通过环境变量 Jwt__SigningKey 注入真实密钥");
        }

        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
    }

    /// <inheritdoc />
    public string IssueToken(string userId, string role = JwtRoles.User, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("用户标识不能为空", nameof(userId));

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Role, role)
        };

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            claims.Add(new(ClaimTypes.Name, displayName));
        }

        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now,
            expires: now.AddMinutes(_options.ExpirationMinutes),
            signingCredentials: new SigningCredentials(_key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <inheritdoc />
    public CurrentPrincipal Validate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return Unauthenticated();
        }

        // 容忍 "Bearer xxx" 格式
        var raw = token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? token[7..].Trim()
            : token;

        try
        {
            var principal = new JwtSecurityTokenHandler().ValidateToken(raw, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _options.Issuer,
                ValidateAudience = true,
                ValidAudience = _options.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = _key,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = ClaimTypes.Name,
                RoleClaimType = ClaimTypes.Role
            }, out _);

            var userId = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                         ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (string.IsNullOrWhiteSpace(userId))
            {
                _logger.LogWarning("令牌缺少用户标识");
                return Unauthenticated();
            }

            return new CurrentPrincipal
            {
                UserId = userId,
                Role = principal.FindFirst(ClaimTypes.Role)?.Value ?? JwtRoles.User,
                DisplayName = principal.FindFirst(ClaimTypes.Name)?.Value ?? userId,
                IsAuthenticated = true
            };
        }
        catch (SecurityTokenExpiredException)
        {
            _logger.LogInformation("令牌已过期");
            return Unauthenticated();
        }
        catch (Exception ex)
        {
            // 不记录令牌内容，避免日志泄漏凭证
            _logger.LogWarning("令牌验证失败: {Reason}", ex.GetType().Name);
            return Unauthenticated();
        }
    }

    private static CurrentPrincipal Unauthenticated() => new()
    {
        UserId = "",
        Role = "",
        IsAuthenticated = false
    };
}
