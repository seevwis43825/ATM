// ===== 契约：银行目录相关契约 =====

using MockBank.Api.Domain;

namespace MockBank.Api.Contracts;

/// <summary>银行目录响应体，描述一家被模拟的银行。</summary>
/// <param name="BankCode">行代码（如 ICBC）。</param>
/// <param name="BankName">银行全称。</param>
/// <param name="ShortName">银行简称。</param>
/// <param name="BinPrefix">本行卡号 BIN 前缀。</param>
/// <param name="BranchName">营业网点名称。</param>
/// <param name="ServiceHotline">客服电话。</param>
/// <param name="DatabaseFileName">本行核心系统的数据库文件名（体现「每行一个库」）。</param>
public sealed record BankResponse(
    string BankCode,
    string BankName,
    string ShortName,
    string BinPrefix,
    string BranchName,
    string ServiceHotline,
    string DatabaseFileName);

/// <summary>银行目录响应体与各行数据规模。</summary>
/// <param name="Bank">银行基本信息。</param>
/// <param name="CustomerCount">本行客户数。</param>
/// <param name="AccountCount">本行账户数。</param>
/// <param name="CardCount">本行银行卡数。</param>
/// <param name="TransactionCount">本行交易流水数。</param>
/// <param name="ProductCount">本行理财产品数。</param>
public sealed record BankOverviewResponse(
    BankResponse Bank,
    int CustomerCount,
    int AccountCount,
    int CardCount,
    int TransactionCount,
    int ProductCount);

/// <summary>银行目录的实体映射辅助方法。</summary>
public static class BankMapping
{
    /// <summary>把银行描述符转换为对外响应体。</summary>
    /// <param name="bank">银行描述符。</param>
    /// <returns>银行目录响应体。</returns>
    public static BankResponse ToResponse(this BankDescriptor bank) =>
        new(bank.BankCode, bank.BankName, bank.ShortName, bank.BinPrefix,
            bank.BranchName, bank.ServiceHotline, bank.DatabaseFileName);
}
