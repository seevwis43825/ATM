// ===== 核心银行 HTTP 客户端 =====
// 插件通过此客户端访问银行系统。这里对接的是 Mock Bank；
// 切换到真实 CBS 时只需修改 BaseUrl 与鉴权方式，插件代码零改动。

namespace BankingAgent.Base.CoreBank;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BankingAgent.PluginSdk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>核心银行连接配置。</summary>
public sealed class CoreBankOptions
{
    /// <summary>核心系统基地址。</summary>
    public string BaseUrl { get; set; } = "http://localhost:5200";
    /// <summary>接口前缀。</summary>
    public string ApiPrefix { get; set; } = "/api/corebank/v1";
    /// <summary>请求超时（秒）。</summary>
    public int TimeoutSeconds { get; set; } = 15;
    /// <summary>内部服务间调用凭证（生产环境应改为 mTLS 或签名）。</summary>
    public string? ServiceToken { get; set; }
}

/// <summary>核心银行 HTTP 客户端实现。</summary>
public sealed class CoreBankClient(
    HttpClient httpClient,
    IOptions<CoreBankOptions> options,
    ILogger<CoreBankClient> logger) : ICoreBankClient
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly CoreBankOptions _options = options.Value;

    private string Url(string path) => _options.BaseUrl.TrimEnd('/') + _options.ApiPrefix + path;

    /// <inheritdoc />
    public async Task<AccountSnapshot?> GetAccountAsync(string accountNo, CancellationToken ct = default)
    {
        var resp = await httpClient.GetAsync(Url($"/accounts/{accountNo}/balance"), ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<AccountDto>(JsonOpts, ct) is { } dto
            ? Map(dto)
            : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountSnapshot>> ListAccountsAsync(string userId, CancellationToken ct = default)
    {
        var resp = await httpClient.GetAsync(Url($"/accounts?userId={userId}"), ct);
        resp.EnsureSuccessStatusCode();
        var list = await resp.Content.ReadFromJsonAsync<List<AccountDto>>(JsonOpts, ct) ?? [];
        return list.Select(Map).ToList();
    }

    /// <inheritdoc />
    public async Task<TransferResult> ExecuteTransferAsync(TransferCommand cmd, CancellationToken ct = default)
    {
        try
        {
            var resp = await httpClient.PostAsJsonAsync(Url("/transfers"), new
            {
                userId = cmd.UserId,
                fromAccountNo = cmd.FromAccountNo,
                toAccountNo = cmd.ToAccountNo,
                amount = cmd.Amount,
                currency = cmd.Currency,
                remark = cmd.Remark
            }, ct);

            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                var err = TryParseError(body);
                logger.LogWarning("转账被核心系统拒绝: {Code} {Message}", err.Code, err.Message);
                return new TransferResult
                {
                    Success = false,
                    ErrorCode = err.Code,
                    ErrorMessage = err.Message,
                    CompletedAt = DateTimeOffset.UtcNow
                };
            }

            var ok = JsonSerializer.Deserialize<TransferOkDto>(body, JsonOpts);
            return new TransferResult
            {
                Success = true,
                TransactionNo = ok?.TransactionNo ?? ok?.TxNo,
                BalanceAfter = ok?.BalanceAfter ?? ok?.FromBalance ?? 0,
                CompletedAt = DateTimeOffset.UtcNow
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogError(ex, "调用核心系统失败");
            return new TransferResult
            {
                Success = false,
                ErrorCode = "CORE_BANK_UNAVAILABLE",
                ErrorMessage = "银行核心系统暂时不可用，请稍后重试",
                CompletedAt = DateTimeOffset.UtcNow
            };
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TransactionRecord>> ListTransactionsAsync(
        TransactionQuery query, CancellationToken ct = default)
    {
        var url = Url($"/transactions?userId={query.UserId}&page={query.Page}&pageSize={query.PageSize}");
        if (!string.IsNullOrEmpty(query.AccountNo)) url += $"&accountNo={query.AccountNo}";
        if (!string.IsNullOrEmpty(query.Category)) url += $"&category={query.Category}";

        var resp = await httpClient.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return [];

        var body = await resp.Content.ReadAsStringAsync(ct);
        var list = ExtractArray(body);
        return list.Select(ParseTransaction).ToList();
    }

    /// <inheritdoc />
    public async Task<MonthlyStatement?> GetMonthlyStatementAsync(
        string accountNo, int year, int month, CancellationToken ct = default)
    {
        var resp = await httpClient.GetAsync(
            Url($"/statements/{accountNo}?month={year:D4}-{month:D2}"), ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();

        var dto = await resp.Content.ReadFromJsonAsync<StatementDto>(JsonOpts, ct);
        if (dto is null) return null;

        // 核心系统不返回占比，此处按总额计算，保证前端展示有意义的数值
        var totalExpense = dto.TotalExpense > 0 ? dto.TotalExpense : 1m;
        var categories = (dto.Categories ?? [])
            .Select(c => new CategoryBreakdown(
                c.Category,
                c.Amount,
                Math.Round(c.Amount / totalExpense, 4)))
            .ToList();

        return new MonthlyStatement
        {
            AccountNo = accountNo,
            Year = year,
            Month = month,
            TotalIncome = dto.TotalIncome,
            TotalExpense = dto.TotalExpense,
            TransactionCount = dto.TransactionCount,
            Categories = categories,
            DailyTrend = (dto.DailyTrend ?? []).Select(d =>
                new DailyTrendPoint(d.Day, d.Amount)).ToList()
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CardSnapshot>> ListCardsAsync(string userId, CancellationToken ct = default)
    {
        var resp = await httpClient.GetAsync(Url($"/cards?userId={userId}"), ct);
        resp.EnsureSuccessStatusCode();
        var list = await resp.Content.ReadFromJsonAsync<List<CardDto>>(JsonOpts, ct) ?? [];
        return list.Select(c => new CardSnapshot
        {
            CardNo = c.CardNo,
            UserId = c.UserId,
            CardType = c.CardType,
            Status = c.Status,
            BoundPhone = c.BoundPhone,
            DailyLimit = c.DailyLimit,
            AccountNo = c.AccountNo
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<CardSnapshot?> UpdateCardStatusAsync(
        string cardNo, string newStatus, string reason, CancellationToken ct = default)
    {
        var resp = await httpClient.PostAsJsonAsync(Url($"/cards/{cardNo}/status"),
            new { newStatus, reason }, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();

        var dto = await resp.Content.ReadFromJsonAsync<CardDto>(JsonOpts, ct);
        if (dto is null) return null;
        return new CardSnapshot
        {
            CardNo = dto.CardNo,
            UserId = dto.UserId,
            CardType = dto.CardType,
            Status = dto.Status,
            BoundPhone = dto.BoundPhone,
            DailyLimit = dto.DailyLimit,
            AccountNo = dto.AccountNo
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WealthProduct>> ListProductsAsync(
        string? riskLevel = null, CancellationToken ct = default)
    {
        var url = Url(string.IsNullOrEmpty(riskLevel) ? "/products" : $"/products?riskLevel={riskLevel}");
        var resp = await httpClient.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var list = await resp.Content.ReadFromJsonAsync<List<ProductDto>>(JsonOpts, ct) ?? [];
        return list.Select(p => new WealthProduct
        {
            Code = p.Code,
            Name = p.Name,
            Type = p.Type,
            RiskLevel = p.RiskLevel,
            AnnualRate = p.AnnualRate,
            MinInvestment = p.MinInvestment,
            TermDays = p.TermDays,
            Status = p.Status
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<SubscriptionResult> SubscribeWealthAsync(
        string userId, string productCode, decimal amount, CancellationToken ct = default)
    {
        var resp = await httpClient.PostAsJsonAsync(Url("/wealth/subscribe"),
            new { userId, productCode, amount }, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            var err = TryParseError(body);
            return new SubscriptionResult { Success = false, ErrorCode = err.Code, ErrorMessage = err.Message };
        }

        var ok = JsonSerializer.Deserialize<SubscriptionOkDto>(body, JsonOpts);
        return new SubscriptionResult { Success = true, SubscriptionNo = ok?.SubscriptionNo ?? ok?.OrderNo };
    }

    // ===== DTO 映射 =====

    private static AccountSnapshot Map(AccountDto a) => new()
    {
        AccountNo = a.AccountNo,
        UserId = a.UserId,
        HolderName = a.HolderName,
        AccountType = a.AccountType,
        Balance = a.Balance,
        CreditLimit = a.CreditLimit,
        CreditUsed = a.CreditUsed,
        Status = a.Status,
        Currency = a.Currency,
        DailyTransferLimit = a.DailyTransferLimit
    };

    private static TransactionRecord ParseTransaction(JsonElement e) => new()
    {
        TransactionNo = Str(e, "transactionNo") ?? Str(e, "txNo") ?? "",
        AccountNo = Str(e, "accountNo") ?? "",
        UserId = Str(e, "userId") ?? "",
        Counterparty = Str(e, "counterparty") ?? Str(e, "counterPartyName") ?? "",
        Amount = Dec(e, "amount"),
        IsCredit = Bool(e, "isCredit") ?? Dec(e, "amount") > 0,
        Category = Str(e, "category") ?? "其他",
        Merchant = Str(e, "merchant"),
        OccurredAt = e.TryGetProperty("occurredAt", out var t) && t.TryGetDateTimeOffset(out var dto)
            ? dto : DateTimeOffset.UtcNow
    };

    private static (string Code, string Message) TryParseError(string body)
    {
        try
        {
            var e = JsonSerializer.Deserialize<JsonElement>(body);
            return (Str(e, "code") ?? "UNKNOWN", Str(e, "message") ?? "未知错误");
        }
        catch
        {
            return ("UNKNOWN", body.Length > 200 ? body[..200] : body);
        }
    }

    /// <summary>兼容接口直接返回数组或包裹对象两种形态。</summary>
    private static List<JsonElement> ExtractArray(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            return doc.RootElement.EnumerateArray().ToList();
        }
        if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            return items.EnumerateArray().ToList();
        }
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            return data.EnumerateArray().ToList();
        }
        return [];
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal Dec(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;

    private static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : null;
}

// ===== 与 Mock Bank 对齐的 DTO =====

internal sealed class AccountDto
{
    public string AccountNo { get; set; } = "";
    public string UserId { get; set; } = "";
    public string HolderName { get; set; } = "";
    public string AccountType { get; set; } = "储蓄卡";
    public decimal Balance { get; set; }
    public decimal? CreditLimit { get; set; }
    public decimal CreditUsed { get; set; }
    public string Status { get; set; } = "正常";
    public string Currency { get; set; } = "CNY";
    public decimal DailyTransferLimit { get; set; } = 500000m;
}

internal sealed class TransferOkDto
{
    public string? TransactionNo { get; set; }
    public string? TxNo { get; set; }
    public decimal BalanceAfter { get; set; }
    public decimal? FromBalance { get; set; }
}

internal sealed class CardDto
{
    public string CardNo { get; set; } = "";
    public string UserId { get; set; } = "";
    public string CardType { get; set; } = "";
    public string Status { get; set; } = "";
    public string? BoundPhone { get; set; }
    public decimal DailyLimit { get; set; }
    public string AccountNo { get; set; } = "";
}

internal sealed class ProductDto
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string RiskLevel { get; set; } = "";
    public decimal AnnualRate { get; set; }
    public decimal MinInvestment { get; set; }
    public int TermDays { get; set; }
    public string Status { get; set; } = "在售";
}

internal sealed class SubscriptionOkDto
{
    public string? SubscriptionNo { get; set; }
    public string? OrderNo { get; set; }
}

internal sealed class StatementDto
{
    public decimal TotalIncome { get; set; }
    public decimal TotalExpense { get; set; }
    public int TransactionCount { get; set; }
    public List<CategoryDto>? Categories { get; set; }
    public List<DailyDto>? DailyTrend { get; set; }
}

internal sealed class CategoryDto
{
    public string Category { get; set; } = "";
    public decimal Amount { get; set; }
    public decimal Ratio { get; set; }
}

internal sealed class DailyDto
{
    public int Day { get; set; }
    public decimal Amount { get; set; }
}
