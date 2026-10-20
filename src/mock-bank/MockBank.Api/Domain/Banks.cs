// ===== 领域模型：模拟银行目录 =====
//
// 设计立场：
//   真实世界里，一个自然人可以在多家银行开户，每家银行是**独立的法人核心系统**，
//   拥有自己的客户主档、账户表、卡表与流水库，彼此之间只通过清算系统往来。
//   本模拟系统照搬这个结构：
//     - BankDescriptor  —— 一家被模拟的银行（行代码、卡号 BIN、网点、独立库文件）
//     - 每个 BankLedger  —— 一家的核心系统实例，持有**自己的 SQLite 数据库**
//     - MockBankStore   —— 跨行聚合门面，把多家银行的数据汇总成一次查询
//   因此「我有哪些卡」可以一次返回工/建/招三行发行的卡，即跨行卡片。

namespace MockBank.Api.Domain;

/// <summary>银行描述符：描述一家被模拟的银行及其核心系统落点。</summary>
public sealed record BankDescriptor
{
    /// <summary>行代码，全局唯一（如 ICBC），用于接口筛选与数据库命名。</summary>
    public required string BankCode { get; init; }

    /// <summary>银行全称（如 中国工商银行）。</summary>
    public required string BankName { get; init; }

    /// <summary>银行简称（如 工商银行），卡片与账户上的发卡行展示用。</summary>
    public required string ShortName { get; init; }

    /// <summary>本行发行的卡号 BIN 前缀，用于按行号段识别卡片归属。</summary>
    public required string BinPrefix { get; init; }

    /// <summary>本行营业网点名称。</summary>
    public required string BranchName { get; init; }

    /// <summary>本行客服电话。</summary>
    public required string ServiceHotline { get; init; }

    /// <summary>本行核心系统的数据库文件名（如 mockbank_icbc.db）。</summary>
    public required string DatabaseFileName { get; init; }
}

/// <summary>模拟银行目录：系统内被模拟的全部银行。</summary>
public static class BankCatalog
{
    /// <summary>被模拟的三家银行。顺序即界面展示顺序。</summary>
    public static readonly IReadOnlyList<BankDescriptor> All =
    [
        new()
        {
            BankCode = "ICBC",
            BankName = "中国工商银行",
            ShortName = "工商银行",
            BinPrefix = "6222",
            BranchName = "工行北京分行营业部",
            ServiceHotline = "95588",
            DatabaseFileName = "mockbank_icbc.db",
        },
        new()
        {
            BankCode = "CCB",
            BankName = "中国建设银行",
            ShortName = "建设银行",
            BinPrefix = "6217",
            BranchName = "建行北京分行营业部",
            ServiceHotline = "95533",
            DatabaseFileName = "mockbank_ccb.db",
        },
        new()
        {
            BankCode = "CMB",
            BankName = "招商银行",
            ShortName = "招商银行",
            BinPrefix = "6225",
            BranchName = "招行北京分行营业部",
            ServiceHotline = "95555",
            DatabaseFileName = "mockbank_cmb.db",
        },
    ];

    /// <summary>按行代码查找银行。</summary>
    /// <param name="bankCode">行代码，忽略大小写；可为空。</param>
    /// <returns>命中的银行描述符；未命中时返回 null。</returns>
    public static BankDescriptor? Find(string? bankCode)
    {
        if (string.IsNullOrWhiteSpace(bankCode)) return null;
        return All.FirstOrDefault(b =>
            string.Equals(b.BankCode, bankCode.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>取银行的简称，未知行代码回退为行代码本身。</summary>
    /// <param name="bankCode">行代码，可为空。</param>
    /// <returns>银行简称；行代码为空时返回空字符串。</returns>
    public static string ShortNameOf(string? bankCode)
    {
        if (string.IsNullOrWhiteSpace(bankCode)) return string.Empty;
        var bank = Find(bankCode);
        return bank?.ShortName ?? bankCode.Trim();
    }

    /// <summary>取银行的全称，未知行代码回退为行代码本身。</summary>
    /// <param name="bankCode">行代码，可为空。</param>
    /// <returns>银行全称；行代码为空时返回空字符串。</returns>
    public static string FullNameOf(string? bankCode)
    {
        if (string.IsNullOrWhiteSpace(bankCode)) return string.Empty;
        var bank = Find(bankCode);
        return bank?.BankName ?? bankCode.Trim();
    }
}
