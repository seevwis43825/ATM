// ===== 安全响应头中间件 =====
// 对应《网络安全法》第 21 条与 OWASP Secure Headers 最佳实践。
// 银行系统必须禁用点击劫持、MIME 混淆、跨域信息泄漏等攻击面。

using Microsoft.AspNetCore.Http;

namespace BankingAgent.Host.Middleware;

/// <summary>注入安全响应头。</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    /// <summary>处理请求。</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var response = context.Response;

        // 禁止浏览器猜测内容类型（防 MIME 混淆 XSS）
        response.Headers["X-Content-Type-Options"] = "nosniff";

        // 禁止被嵌入 iframe（防点击劫持，金融系统强制）
        response.Headers["X-Frame-Options"] = "DENY";
        response.Headers["frame-ancestors"] = "'none'";

        // 跨域策略：只允许同源，不泄露完整 URL（含查询参数中的敏感信息）
        response.Headers["Referrer-Policy"] = "no-referrer";

        // 关闭不必要的浏览器能力
        response.Headers["Permissions-Policy"] =
            "accelerometer=(), camera=(), geolocation=(), gyroscope=(), " +
            "microphone=(), payment=(), usb=(), payment-request=()";

        // 强制 HTTPS（仅在已建立 TLS 时下发，避免本地开发死循环）
        if (context.Request.IsHttps)
        {
            response.Headers["Strict-Transport-Security"] =
                "max-age=31536000; includeSubDomains; preload";
        }

        // 移除泄露技术栈的默认头
        response.Headers.Remove("X-Powered-By");
        response.Headers.Remove("Server");
        response.Headers.Remove("X-AspNet-Version");

        // Content-Security-Policy：本系统以 JSON API 为主，限定同源
        response.Headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
            "img-src 'self' data:; connect-src 'self'; font-src 'self'; " +
            "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

        // 跨域资源策略
        response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";

        // 禁用 DNS prefetch，防数据外泄探测
        response.Headers["X-DNS-Prefetch-Control"] = "off";

        // 清除 ASP.NET Core 诊断信息
        response.Headers.Remove("X-Execution-Id");

        await next(context);
    }
}
