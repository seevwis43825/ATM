// ===== 契约：银行卡查询、状态变更与限额调整契约 =====

using MockBank.Api.Domain;

namespace MockBank.Api.Contracts;

/// <summary>银行卡查询响应体。</summary>
/// <param name="CardNo">卡号。</param>
/// <param name="AccountNo">绑定账号。</param>
/// <param name="UserId">客户号。</param>
/// <param name="CardType">卡类型中文名（借记卡 / 信用卡）。</param>
/// <param name="Status">卡状态中文名（正常 / 挂失 / 冻结）。</param>
/// <param name="BoundPhone">绑定手机号。</param>
/// <param name="DailyLimit">单日交易限额。</param>
/// <param name="IssueDate">发卡日期。</param>
/// <param name="ExpiryDate">有效期。</param>
/// <param name="CardOrg">发卡行。</param>
/// <param name="StatusReason">最近一次状态变更原因。</param>
/// <param name="StatusChangedAt">最近一次状态变更时间。</param>
public sealed record CardResponse(
    string CardNo,
    string AccountNo,
    string UserId,
    string CardType,
    string Status,
    string BoundPhone,
    decimal DailyLimit,
    DateTimeOffset IssueDate,
    DateTimeOffset ExpiryDate,
    string CardOrg,
    string? StatusReason,
    DateTimeOffset? StatusChangedAt);

/// <summary>卡状态变更请求体。</summary>
/// <param name="NewStatus">目标状态：正常 / 挂失 / 冻结 / 解挂 / 解冻。</param>
/// <param name="Reason">变更原因。</param>
public sealed record CardStatusChangeRequest(string? NewStatus, string? Reason = null);

/// <summary>卡状态变更响应体。</summary>
/// <param name="CardNo">卡号。</param>
/// <param name="PreviousStatus">变更前状态中文名。</param>
/// <param name="Status">变更后状态中文名。</param>
/// <param name="Reason">变更原因。</param>
/// <param name="ChangedAt">变更时间。</param>
/// <param name="Message">结果描述。</param>
public sealed record CardStatusChangeResponse(
    string CardNo,
    string PreviousStatus,
    string Status,
    string? Reason,
    DateTimeOffset ChangedAt,
    string Message = "卡状态变更成功");

/// <summary>卡限额调整请求体。</summary>
/// <param name="DailyLimit">新的单日交易限额（元），必须大于 0。</param>
/// <param name="Reason">调整原因。</param>
public sealed record CardLimitAdjustRequest(decimal DailyLimit, string? Reason = null);

/// <summary>卡限额调整响应体。</summary>
/// <param name="CardNo">卡号。</param>
/// <param name="AccountNo">绑定账号。</param>
/// <param name="PreviousDailyLimit">调整前单日限额。</param>
/// <param name="DailyLimit">调整后单日限额。</param>
/// <param name="Reason">调整原因。</param>
/// <param name="AdjustedAt">调整时间。</param>
/// <param name="Message">结果描述。</param>
public sealed record CardLimitAdjustResponse(
    string CardNo,
    string AccountNo,
    decimal PreviousDailyLimit,
    decimal DailyLimit,
    string? Reason,
    DateTimeOffset AdjustedAt,
    string Message = "限额调整成功");

/// <summary>银行卡实体的 DTO 映射辅助方法。</summary>
public static class CardMapping
{
    /// <summary>把卡片实体转换为卡查询响应体。</summary>
    /// <param name="card">卡片实体。</param>
    /// <returns>卡查询响应体。</returns>
    public static CardResponse ToResponse(this BankCard card) =>
        new(card.CardNo, card.AccountNo, card.UserId, card.CardTypeText, card.StatusText, card.BoundPhone,
            card.DailyLimit, card.IssueDate, card.ExpiryDate, card.CardOrg, card.StatusReason, card.StatusChangedAt);
}
