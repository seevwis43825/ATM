// ===== 领域模型：银行客户（自然人）主档，含客户风险等级 R1-R4 =====

namespace MockBank.Api.Domain;

/// <summary>客户风险等级。数值越大风险越高，R4 为最高风险。</summary>
public enum RiskLevel
{
    /// <summary>R1 保守型客户。</summary>
    R1 = 1,

    /// <summary>R2 稳健型客户。</summary>
    R2 = 2,

    /// <summary>R3 平衡型客户。</summary>
    R3 = 3,

    /// <summary>R4 进取型客户。</summary>
    R4 = 4,
}

/// <summary>银行客户（自然人）实体，模拟核心系统的客户主档。</summary>
public sealed record Customer
{
    /// <summary>客户号，全局唯一（演示数据形如 u_demo01）。</summary>
    public required string UserId { get; init; }

    /// <summary>客户姓名。</summary>
    public required string Name { get; init; }

    /// <summary>身份证号。</summary>
    public required string IdCardNo { get; init; }

    /// <summary>绑定手机号。</summary>
    public required string Phone { get; init; }

    /// <summary>客户风险等级（R1-R4），决定可购买理财产品的风险上限。</summary>
    public RiskLevel RiskLevel { get; init; } = RiskLevel.R2;

    /// <summary>客户等级，如 VIP / 普通 / 银卡。</summary>
    public string CustomerLevel { get; init; } = "普通";

    /// <summary>开户网点名称。</summary>
    public string BranchName { get; init; } = "模拟银行营业部";

    /// <summary>开户时间。</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>风险等级的中文描述，如“平衡型”。</summary>
    public string RiskLevelText => RiskLevel.ToText();
}

/// <summary>风险等级的解析与格式化辅助方法。</summary>
public static class RiskLevelExtensions
{
    /// <summary>把 "R3" / "3" / "平衡" 之类的文本解析为 <see cref="RiskLevel"/>。</summary>
    /// <param name="value">待解析文本。</param>
    /// <param name="level">解析成功时输出风险等级。</param>
    /// <returns>解析成功返回 true。</returns>
    public static bool TryParseRiskLevel(string? value, out RiskLevel level)
    {
        level = RiskLevel.R2;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.StartsWith('R') || text.StartsWith('r'))
        {
            text = text[1..];
        }

        switch (text)
        {
            case "1":
            case "保守":
                level = RiskLevel.R1;
                return true;
            case "2":
            case "稳健":
                level = RiskLevel.R2;
                return true;
            case "3":
            case "平衡":
                level = RiskLevel.R3;
                return true;
            case "4":
            case "进取":
                level = RiskLevel.R4;
                return true;
            default:
                return false;
        }
    }

    /// <summary>把风险等级格式化为 "R3" 形式。</summary>
    /// <param name="level">风险等级。</param>
    /// <returns>形如 "R3" 的字符串。</returns>
    public static string ToText(this RiskLevel level) => $"R{(int)level}";
}
