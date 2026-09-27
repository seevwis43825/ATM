// ===== 领域模型：银行理财产品主档（稳健 / 平衡 / 进取，R1-R4 风险等级）=====

namespace MockBank.Api.Domain;

/// <summary>理财产品类型。</summary>
public enum ProductType
{
    /// <summary>稳健型。</summary>
    Steady = 1,

    /// <summary>平衡型。</summary>
    Balanced = 2,

    /// <summary>进取型。</summary>
    Aggressive = 3,
}

/// <summary>理财产品销售状态。</summary>
public enum ProductStatus
{
    /// <summary>在售。</summary>
    OnSale = 1,

    /// <summary>已售罄。</summary>
    SoldOut = 2,

    /// <summary>已下线。</summary>
    Closed = 3,
}

/// <summary>理财产品实体，模拟核心系统的理财产品表。</summary>
public sealed record WealthProduct
{
    /// <summary>产品代码，形如 WP001。</summary>
    public required string Code { get; init; }

    /// <summary>产品名称。</summary>
    public required string Name { get; init; }

    /// <summary>产品类型（稳健 / 平衡 / 进取）。</summary>
    public required ProductType Type { get; init; }

    /// <summary>产品风险等级（R1-R4）。</summary>
    public required RiskLevel RiskLevel { get; init; }

    /// <summary>年化收益率（百分数，3.20 表示 3.20%）。</summary>
    public decimal AnnualRate { get; init; }

    /// <summary>起投金额（元）。</summary>
    public decimal MinAmount { get; init; }

    /// <summary>单笔最高认购金额（元）。</summary>
    public decimal MaxAmount { get; init; } = 1_000_000m;

    /// <summary>产品期限（天）。</summary>
    public int TermDays { get; init; }

    /// <summary>销售状态。</summary>
    public ProductStatus Status { get; init; } = ProductStatus.OnSale;

    /// <summary>产品单位净值。</summary>
    public decimal NetAssetValue { get; init; } = 1.0000m;

    /// <summary>募集规模上限（元）。</summary>
    public decimal TotalRaiseLimit { get; init; } = 100_000_000m;

    /// <summary>已募集金额（元）。</summary>
    public decimal RaisedAmount { get; init; }

    /// <summary>发售开始日期。</summary>
    public DateTimeOffset SaleStartDate { get; init; }

    /// <summary>发售结束日期。</summary>
    public DateTimeOffset SaleEndDate { get; init; }

    /// <summary>风险提示语。</summary>
    public string RiskNote { get; init; } = "本产品为模拟演示数据，不构成任何投资建议。";

    /// <summary>产品类型的中文描述。</summary>
    public string TypeText => Type.ToText();

    /// <summary>产品销售状态的中文描述。</summary>
    public string StatusText =>
        Status switch
        {
            ProductStatus.OnSale => "在售",
            ProductStatus.SoldOut => "已售罄",
            ProductStatus.Closed => "已下线",
            _ => "未知",
        };

    /// <summary>剩余可募集金额。</summary>
    public decimal RemainingAmount => Math.Max(0m, TotalRaiseLimit - RaisedAmount);
}

/// <summary>理财产品类型文本解析辅助方法。</summary>
public static class ProductTypeExtensions
{
    /// <summary>把 "稳健" / "steady" 之类的文本解析为 <see cref="ProductType"/>。</summary>
    /// <param name="value">待解析文本。</param>
    /// <param name="type">解析成功时输出产品类型。</param>
    /// <returns>解析成功返回 true。</returns>
    public static bool TryParseProductType(string? value, out ProductType type)
    {
        type = ProductType.Steady;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "steady":
            case "稳健":
            case "保守":
                type = ProductType.Steady;
                return true;
            case "balanced":
            case "balance":
            case "平衡":
                type = ProductType.Balanced;
                return true;
            case "aggressive":
            case "进取":
            case "激进":
                type = ProductType.Aggressive;
                return true;
            default:
                return false;
        }
    }

    /// <summary>把产品类型格式化为中文。</summary>
    /// <param name="type">产品类型。</param>
    /// <returns>稳健 / 平衡 / 进取。</returns>
    public static string ToText(this ProductType type) =>
        type switch
        {
            ProductType.Steady => "稳健",
            ProductType.Balanced => "平衡",
            ProductType.Aggressive => "进取",
            _ => "未知",
        };
}
