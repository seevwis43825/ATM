// ===== 速率限制中间件（ASP.NET Core 层）=====
// 算法实现在 Base/RateLimiter.cs（纯逻辑，不依赖 ASP.NET Core）。
// 本文件负责把限流接入 HTTP 管道。

using BankingAgent.Base.Security.Auth;
using BankingAgent.Base.Security.RateLimit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace BankingAgent.Host.Middleware;

/// <summary>速率限制中间件。双维度：按 IP（防扫描）+ 按身份（防单账号爆破）。</summary>
public sealed class RateLimitingMiddleware(RequestDelegate next, ILogger<RateLimitingMiddleware> logger)
{
    /// <summary>执行限流检查。</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var limiter = context.RequestServices.GetRequiredService<RateLimiter>();
        var options = context.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;

        if (!options.Enabled)
        {
            await next(context);
            return;
        }

        var tier = Classify(context.Request.Path, context.Request.Method);
        if (tier == RateLimitTier.None)
        {
            await next(context);
            return;
        }

        var (permits, burst) = ResolveQuota(tier, options);
        var ip = ResolveClientIp(context);
        var identity = ResolveIdentity(context);

        // 维度 1：按 IP，防全站泛刷与端口扫描
        var ipDecision = limiter.TryAcquire("ip", ip, options.IpPermitsPerMinute, options.BurstCapacity * 4);
        if (!ipDecision.Allowed)
        {
            await RejectAsync(context, ipDecision, "ip");
            return;
        }

        if (!string.IsNullOrEmpty(identity))
        {
            // 维度 2：按身份，防单账号暴力破解
            var userDecision = limiter.TryAcquire("user", identity, permits, burst);
            if (!userDecision.Allowed)
            {
                await RejectAsync(context, userDecision, "identity");
                return;
            }

            context.Response.Headers["X-RateLimit-Limit"] = permits.ToString();
            context.Response.Headers["X-RateLimit-Remaining"] = userDecision.Remaining.ToString();
        }
        else
        {
            context.Response.Headers["X-RateLimit-Limit"] = options.IpPermitsPerMinute.ToString();
            context.Response.Headers["X-RateLimit-Remaining"] = ipDecision.Remaining.ToString();
        }

        await next(context);
    }

    /// <summary>按端点路径与敏感度分档。</summary>
    private static RateLimitTier Classify(PathString path, string method)
    {
        var p = path.Value?.ToLowerInvariant() ?? "";

        if (p.Contains("/health") || p.Contains("/api/auth/me"))
            return RateLimitTier.None;

        // 认证端点最严格，防凭证爆破
        if (p.Contains("/api/auth/token"))
            return RateLimitTier.Strict;

        // 资金类
        if (p.Contains("transfer") || p.Contains("confirm") || p.Contains("wealth"))
            return RateLimitTier.Standard;

        // 只读查询
        if (p.Contains("/api/plugins") || p.Contains("/api/trajectory"))
            return method == "GET" ? RateLimitTier.ReadOnly : RateLimitTier.Standard;

        return RateLimitTier.Relaxed;
    }

    /// <summary>
    /// 计算某档位的配额 (每分钟许可数, 令牌桶容量)。
    ///
    /// 关键约束：**桶容量绝不允许超过该档位每分钟的许可数**。
    /// 令牌桶按 permitsPerMinute/60 每秒回填，若容量大于每分钟许可数，
    /// 桶就变成一个"蓄水池"：攒够之后可以一次性全部放行，
    /// 且重新攒满需要远超一分钟 —— 限流形同虚设。
    ///
    /// 之前 Strict 档用 Math.Max(3, BurstCapacity / 10) 从**全局**突发放大系数
    /// 推导，全局值一调大（为缓解普通接口的突发），登录接口的防爆破能力
    /// 会被同步削弱（实测：10 次连续取令牌全部放行）。因此改为各档位显式给出，
    /// 全局 BurstCapacity 只作为"上限"参与 min，不再能放大严格档位。
    /// </summary>
    private static (int Permits, int Burst) ResolveQuota(RateLimitTier tier, RateLimitOptions o) => tier switch
    {
        // 认证：防口令爆破，桶容量必须等于每分钟许可数（默认 5）
        RateLimitTier.Strict => Quota(o.AuthPermitsPerMinute, o),

        // 资金操作：允许一定突发，但不超过每分钟许可数
        RateLimitTier.Standard => Quota(o.TransferPermitsPerMinute, o),

        // 只读：读多写少，突发上限即每分钟许可数
        RateLimitTier.ReadOnly => Quota(o.ReadPermitsPerMinute, o),

        // 普通业务
        _ => Quota(o.ApiPermitsPerMinute, o)
    };

    /// <summary>
    /// 组装单个档位的配额：桶容量的上限是「该档位每分钟许可数」，
    /// 再与全局 BurstCapacity 取小。这样全局参数只能收紧、不能放宽严格档位。
    /// </summary>
    private static (int Permits, int Burst) Quota(int permitsPerMinute, RateLimitOptions o)
    {
        var permits = Math.Max(1, permitsPerMinute);
        var burst = Math.Min(Math.Max(1, o.BurstCapacity), permits);
        return (permits, burst);
    }

    /// <summary>解析客户端 IP。生产部署在反向代理后需配置可信代理白名单。</summary>
    private static string ResolveClientIp(HttpContext context)
    {
        if (context.Connection.RemoteIpAddress is { } remote)
        {
            return remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4().ToString() : remote.ToString();
        }
        return "unknown";
    }

    /// <summary>解析调用方身份。已认证用真实 userId，未认证用令牌哈希（避免匿名请求共享配额）。</summary>
    private static string? ResolveIdentity(HttpContext context)
    {
        if (context.Items["Principal"] is CurrentPrincipal p && p.IsAuthenticated)
        {
            return p.UserId;
        }

        // 未认证场景（登录接口）改用「目标账号 + 客户端 IP」作为维度，
        // 否则同一 NAT 出口下的所有用户会共享同一个 IP 桶，
        // 合法用户会被无辜牵连；反之攻击者换 IP 又可绕过。
        var body = ReadBody(context);
        var userId = TryExtractUserId(body);
        var ip = ResolveClientIp(context);

        if (!string.IsNullOrEmpty(userId))
        {
            return $"login:{ip}:{userId}";
        }

        // 无 body 的未认证请求，退回按 IP + 请求头哈希
        var header = context.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(header))
        {
            var hash = System.Security.Cryptography.SHA256
                .HashData(System.Text.Encoding.UTF8.GetBytes(header));
            return "anon:" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
        }

        return $"ip-only:{ip}";
    }

    /// <summary>读取请求体（限流时需要，之后重置流位置供下游读取）。</summary>
    private static string ReadBody(HttpContext context)
    {
        if (context.Request.ContentLength is null or 0) return string.Empty;

        try
        {
            context.Request.EnableBuffering();
            context.Request.Body.Position = 0;
            using var reader = new StreamReader(
                context.Request.Body, leaveOpen: true);
            var text = reader.ReadToEnd();
            // 重置位置，保证下游模型绑定能正常读取
            context.Request.Body.Position = 0;
            return text;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>从 JSON 请求体中提取 userId（避免引入完整 JSON 解析开销）。</summary>
    private static string? TryExtractUserId(string body)
    {
        if (string.IsNullOrEmpty(body)) return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            foreach (var key in new[] { "userId", "userName", "account" })
            {
                if (doc.RootElement.TryGetProperty(key, out var v)
                    && v.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return v.GetString();
                }
            }
        }
        catch
        {
            // body 非 JSON，忽略
        }
        return null;
    }

    private async Task RejectAsync(HttpContext context, RateLimitDecision decision, string by)
    {
        var retrySeconds = (int)Math.Ceiling(decision.RetryAfter.TotalSeconds);
        context.Response.Headers["Retry-After"] = retrySeconds.ToString();
        context.Response.Headers["X-RateLimit-Limit"] = decision.Limit.ToString();
        context.Response.Headers["X-RateLimit-Remaining"] = "0";
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.ContentType = "application/json; charset=utf-8";

        await context.Response.WriteAsync(
            $"{{\"code\":\"RATE_LIMITED\",\"message\":\"请求过于频繁，请稍后重试\",\"dimension\":\"{by}\",\"retryAfterSeconds\":{retrySeconds}}}");

        logger.LogWarning("限流触发: 维度={Dimension} 路径={Path} 重试={Retry}s",
            by, context.Request.Path, retrySeconds);
    }
}
