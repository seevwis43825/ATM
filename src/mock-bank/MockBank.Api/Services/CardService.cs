// ===== 卡片服务：卡列表查询、挂失/冻结等状态变更与单日限额调整 =====

using MockBank.Api.Contracts;
using MockBank.Api.Data;
using MockBank.Api.Domain;

namespace MockBank.Api.Services;

/// <summary>银行卡管理服务。</summary>
/// <param name="store">模拟银行账本。</param>
public sealed class CardService(MockBankStore store)
{
    /// <summary>查询指定客户名下的全部卡片。</summary>
    /// <param name="userId">客户号。</param>
    /// <returns>卡片响应列表。</returns>
    public IReadOnlyList<CardResponse> GetCardsByUser(string? userId) =>
        store.GetCardsByUser(userId).Select(c => c.ToResponse()).ToList();

    /// <summary>查询单张卡片。</summary>
    /// <param name="cardNo">卡号。</param>
    /// <returns>卡片响应；不存在时返回 null。</returns>
    public CardResponse? GetCard(string? cardNo) => store.GetCard(cardNo)?.ToResponse();

    /// <summary>变更卡片状态（挂失 / 解挂 / 冻结 / 解冻 / 恢复正常）。</summary>
    /// <param name="cardNo">卡号。</param>
    /// <param name="request">状态变更请求。</param>
    /// <param name="error">校验失败时输出错误码。</param>
    /// <param name="message">校验失败时输出中文错误描述。</param>
    /// <returns>变更成功时返回变更结果；失败时返回 null。</returns>
    public CardStatusChangeResponse? ChangeStatus(
        string? cardNo,
        CardStatusChangeRequest? request,
        out string error,
        out string message)
    {
        error = string.Empty;
        message = string.Empty;

        var card = store.GetCard(cardNo);
        if (card is null)
        {
            error = ErrorCodes.CARD_NOT_FOUND;
            message = $"卡号 {cardNo} 不存在。";
            return null;
        }

        if (request is null || !CardEnums.TryParseCardStatus(request.NewStatus, out var newStatus))
        {
            error = ErrorCodes.VALIDATION_ERROR;
            message = $"不支持的卡状态：{request?.NewStatus}。支持值：{string.Join(" / ", CardEnums.SupportedStatusCommands)}。";
            return null;
        }

        var previous = card.StatusText;
        if (card.Status == newStatus)
        {
            error = ErrorCodes.VALIDATION_ERROR;
            message = $"卡片已处于 {newStatus.ToTextForCard()} 状态，无需重复变更。";
            return null;
        }

        var now = DateTimeOffset.Now;
        var updated = card with
        {
            Status = newStatus,
            StatusReason = string.IsNullOrWhiteSpace(request.Reason) ? $"{newStatus.ToTextForCard()}（未填写原因）" : request.Reason!.Trim(),
            StatusChangedAt = now,
        };
        store.UpdateCard(updated);

        message = $"卡 {card.CardNo} 状态已由 {previous} 变更为 {updated.StatusText}";
        return new CardStatusChangeResponse(card.CardNo, previous, updated.StatusText, updated.StatusReason, now, message);
    }

    /// <summary>调整卡片单日交易限额（同时联动所属账户限额）。</summary>
    /// <param name="cardNo">卡号。</param>
    /// <param name="request">限额调整请求。</param>
    /// <param name="error">校验失败时输出错误码。</param>
    /// <param name="message">校验失败时输出中文错误描述。</param>
    /// <returns>调整成功时返回调整结果；失败时返回 null。</returns>
    public CardLimitAdjustResponse? AdjustLimit(
        string? cardNo,
        CardLimitAdjustRequest? request,
        out string error,
        out string message)
    {
        error = string.Empty;
        message = string.Empty;

        var card = store.GetCard(cardNo);
        if (card is null)
        {
            error = ErrorCodes.CARD_NOT_FOUND;
            message = $"卡号 {cardNo} 不存在。";
            return null;
        }

        if (request is null || request.DailyLimit <= 0m)
        {
            error = ErrorCodes.VALIDATION_ERROR;
            message = "单日限额必须大于 0。";
            return null;
        }

        if (request.DailyLimit > 2_000_000m)
        {
            error = ErrorCodes.VALIDATION_ERROR;
            message = "单日限额不得超过 2000000.00 元。";
            return null;
        }

        var previous = card.DailyLimit;
        if (previous == request.DailyLimit)
        {
            error = ErrorCodes.VALIDATION_ERROR;
            message = $"单日限额已为 {previous:F2} 元，无需调整。";
            return null;
        }

        var now = DateTimeOffset.Now;
        var updated = card with
        {
            DailyLimit = request.DailyLimit,
            StatusReason = string.IsNullOrWhiteSpace(request.Reason) ? "限额调整" : request.Reason!.Trim(),
            StatusChangedAt = now,
        };
        store.UpdateCard(updated);

        var account = store.GetAccount(card.AccountNo);
        if (account is not null)
        {
            store.UpdateAccount(account with { DailyLimit = request.DailyLimit });
        }

        message = $"卡 {card.CardNo} 单日限额已由 {previous:F2} 调整为 {request.DailyLimit:F2} 元";
        return new CardLimitAdjustResponse(card.CardNo, card.AccountNo, previous, request.DailyLimit,
            updated.StatusReason, now, message);
    }
}

/// <summary>卡状态到中文文本的扩展方法（供服务层提示使用）。</summary>
internal static class CardStatusTextExtensions
{
    /// <summary>把卡状态格式化为中文。</summary>
    /// <param name="status">卡状态。</param>
    /// <returns>正常 / 挂失 / 冻结。</returns>
    internal static string ToTextForCard(this CardStatus status) =>
        status switch
        {
            CardStatus.Normal => "正常",
            CardStatus.Lost => "挂失",
            CardStatus.Frozen => "冻结",
            _ => "未知",
        };
}
