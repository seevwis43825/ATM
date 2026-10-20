// ===== 契约：客户档案查询契约 =====

using MockBank.Api.Domain;

namespace MockBank.Api.Contracts;

/// <summary>客户档案响应体。</summary>
/// <param name="UserId">客户号。</param>
/// <param name="BankCode">开户行代码（如 ICBC）。</param>
/// <param name="BankName">开户行全称。</param>
/// <param name="Name">客户姓名。</param>
/// <param name="RiskLevel">客户风险等级（R1-R4，行内评级）。</param>
/// <param name="CustomerLevel">客户等级（VIP / 金卡 / 普通等）。</param>
/// <param name="BranchName">开户网点。</param>
public sealed record CustomerResponse(
    string UserId,
    string BankCode,
    string BankName,
    string Name,
    string RiskLevel,
    string CustomerLevel,
    string BranchName);

/// <summary>把客户实体映射为对外响应体。</summary>
public static class CustomerMapping
{
    /// <summary>实体转 DTO。</summary>
    /// <param name="customer">客户实体。</param>
    /// <returns>客户档案响应体。</returns>
    public static CustomerResponse ToResponse(this Customer customer) =>
        new(customer.UserId,
            customer.BankCode,
            BankCatalog.FullNameOf(customer.BankCode),
            customer.Name,
            customer.RiskLevel.ToText(),
            customer.CustomerLevel,
            customer.BranchName);
}
