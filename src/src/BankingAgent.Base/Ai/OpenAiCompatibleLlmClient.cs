// ===== OpenAI 兼容协议客户端 =====
// 一个 baseUrl + 一个 model + 一个 key 即可对接 DeepSeek / 通义千问 / 智谱 /
// Kimi / 本地 Ollama（均提供 OpenAI 兼容的 /chat/completions）。

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BankingAgent.Base.Ai;

/// <summary>OpenAI 兼容协议的模型客户端。</summary>
public sealed class OpenAiCompatibleLlmClient(
    HttpClient httpClient,
    IOptions<LlmOptions> options,
    ILogger<OpenAiCompatibleLlmClient> logger) : ILlmClient
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly LlmOptions _options = options.Value;

    /// <inheritdoc />
    public bool IsAvailable => _options.IsConfigured;

    /// <inheritdoc />
    public string Model => _options.Model;

    /// <inheritdoc />
    public async Task<string?> CompleteAsync(
        string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        if (!IsAvailable) return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
            {
                Content = JsonContent.Create(new
                {
                    model = _options.Model,
                    temperature = _options.Temperature,
                    messages = new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userPrompt }
                    }
                }, options: JsonOpts)
            };
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_options.ApiKey}");

            var resp = await httpClient.SendAsync(request, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                // 状态码与响应体一起记，便于区分「密钥错」「余额不足」「被限流」
                logger.LogWarning("模型调用失败：HTTP {Status} {Body}",
                    (int)resp.StatusCode, Truncate(body, 200));
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                logger.LogWarning("模型返回不含 choices：{Body}", Truncate(body, 200));
                return null;
            }

            return choices[0].TryGetProperty("message", out var message)
                   && message.TryGetProperty("content", out var content)
                   && content.ValueKind == JsonValueKind.String
                ? content.GetString()
                : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 模型不可用绝不能影响主流程：记日志后返回 null，由分类器降级到规则表
            logger.LogWarning(ex, "模型调用异常，将降级到规则表");
            return null;
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";
}
