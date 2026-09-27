// ===== 脱敏与数据分级工具 =====
// 对应 docs/05-security-compliance/03-data-classification.md 的实现。
// L4 字段永不返回明文；L3 字段默认脱敏。

namespace BankingAgent.Base.Security;

using System.Text.RegularExpressions;
using BankingAgent.PluginSdk;

/// <summary>数据脱敏工具。所有对外输出、日志、LLM 上下文都必须经过此处理。</summary>
public static partial class DataMasker
{
    // ===== 基础脱敏 =====

    /// <summary>手机号：138****8000</summary>
    [GeneratedRegex(@"^(\d{3})\d{4}(\d{4})$")]
    private static partial Regex PhonePattern();

    // ===== 身份证脱敏 =====
    // 18 位：前 6 位地址码 + 8 位出生日期 + 3 位顺序码 + 1 位校验码（数字或 X/x）
    // 第 2 个捕获组保留末 4 位（前 3 位顺序码 + 校验码）
    [GeneratedRegex(@"^(\d{6})\d{8}(\d{3}[\dXx])$")]
    private static partial Regex IdCardPattern();

    /// <summary>脱敏手机号。格式不匹配时全部遮蔽。</summary>
    public static string MaskPhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return "****";
        return PhonePattern().IsMatch(phone)
            ? PhonePattern().Replace(phone, "$1****$2")
            : "****";
    }

    /// <summary>脱敏身份证：保留前 6 后 4。</summary>
    public static string MaskIdCard(string? idCard)
    {
        if (string.IsNullOrWhiteSpace(idCard)) return "****";
        return IdCardPattern().IsMatch(idCard)
            ? IdCardPattern().Replace(idCard, "$1********$2")
            : "****";
    }

    /// <summary>银行卡：仅保留后 4 位。</summary>
    public static string MaskBankCard(string? cardNo)
    {
        if (string.IsNullOrWhiteSpace(cardNo) || cardNo.Length < 4) return "****";
        var tail = cardNo[^4..];
        return $"**** **** **** {tail}";
    }

    /// <summary>姓名：张* / 欧阳**</summary>
    public static string MaskName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "**";
        return name.Length == 1 ? name : name[..1] + new string('*', name.Length - 1);
    }

    /// <summary>邮箱：a***@example.com</summary>
    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrEmpty(email)) return "***@***";
        var at = email.IndexOf('@');
        if (at <= 0) return "***@***";
        return email[..1] + "***" + email[at..];
    }

    /// <summary>账号：保留后 4 位。</summary>
    public static string MaskAccount(string? accountNo) =>
        string.IsNullOrWhiteSpace(accountNo) || accountNo.Length <= 4
            ? "****"
            : new string('*', accountNo.Length - 4) + accountNo[^4..];

    // ===== 按敏感级别统一处理 =====

    /// <summary>
    /// 按数据敏感级别选择脱敏策略。
    /// L1 不脱敏；L2 轻度；L3 中度；L4 永不返回原文。
    /// </summary>
    public static string Apply(string? value, DataClassification level, string fieldKind)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return level switch
        {
            DataClassification.L1 => value,
            DataClassification.L4 => "********",
            _ => fieldKind.ToLowerInvariant() switch
            {
                "phone" => MaskPhone(value),
                "idcard" or "id" => MaskIdCard(value),
                "card" or "cardno" or "bankcard" => MaskBankCard(value),
                "account" or "accountno" => MaskAccount(value),
                "name" => MaskName(value),
                "email" => MaskEmail(value),
                _ => MaskAccount(value)
            }
        };
    }

    /// <summary>递归脱敏对象图。用于把实体转成可安全展示的 DTO。</summary>
    public static object? MaskGraph(object? input, DataClassification maxLevel)
    {
        if (input is null) return null;
        if (maxLevel >= DataClassification.L4) return "********";
        return input switch
        {
            string s => Apply(s, maxLevel, "generic"),
            decimal or int or long or bool or DateTimeOffset or Guid => input,            // 字典必须把「键名」传给值，才能做字段感知的脱敏
            // （否则 phone 字段会被当成 generic 走账号掩码）
            IDictionary<string, object?> map => map.ToDictionary(
                entry => entry.Key,
                entry => entry.Value is string s
                    ? Apply(s, maxLevel, entry.Key)
                    : MaskGraph(entry.Value, maxLevel)),
            System.Collections.IEnumerable seq and not string => seq
                .Cast<object?>()
                .Select(x => MaskGraph(x, maxLevel))
                .ToList(),
            _ => input
        };
    }
}
