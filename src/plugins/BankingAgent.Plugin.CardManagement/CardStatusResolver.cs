// ===== 卡片状态语义解析器 =====
//
// 它解决的问题：
//   真实用户不会说「请把卡 6222020200000001 挂失」，他们说「我的卡不见了」。
//   过去插件只在原文里搜「挂失 / 冻结 / 解挂」三个**字面词**，搜不到就当成只读查询，
//   于是「我的卡丢了」只会返回一张卡片列表 —— 语义识别对了，功能却没触发。
//   这正是「识别了语义却没做出对应功能」在卡片场景的具体形态。
//
// 两个设计约束：
//   1. **只做识别，不做执行**。解析器只回答「用户想做什么」，
//      执行仍走插件既有的合规守卫 → 审计 → 核心系统链路。
//   2. **宁可少判，不可错判**。写操作是副作用，误判会真的把用户的卡冻结掉。
//      因此只收录语义明确的说法（「不能用了」这种含糊表述不收录），
//      并且尊重否定词：「别挂失」不会被当成挂失。

namespace BankingAgent.Plugin.CardManagement;

/// <summary>用户对卡片状态提出的操作诉求。</summary>
public enum CardStatusAction
{
    /// <summary>没有识别到写操作诉求，按只读查询处理。</summary>
    None = 0,

    /// <summary>挂失（卡丢了、被偷、找不到）。</summary>
    ReportLost = 1,

    /// <summary>解挂（撤销之前的挂失，卡已找回）。</summary>
    UndoLost = 2,

    /// <summary>冻结（止付、锁定，暂停使用）。</summary>
    Freeze = 3,

    /// <summary>解冻（解除之前的冻结）。</summary>
    Unfreeze = 4,

    /// <summary>调整单日限额。</summary>
    AdjustLimit = 5,
}

/// <summary>把用户原话解析成卡片状态操作诉求。</summary>
public static class CardStatusResolver
{
    /// <summary>
    /// 否定标记。出现在关键词前面时，该说法不算数 ——
    /// 「别挂失」「先不要冻结」表达的是**不做**。
    /// </summary>
    private static readonly string[] NegationMarkers =
    [
        "不要", "不想", "不用", "别", "甭", "先不", "暂不",
        "取消", "撤销", "停止", "不必", "无需"
    ];

    /// <summary>
    /// 各类操作的说法表。
    ///
    /// 顺序很重要：带「取消 / 解」的恢复类说法必须排在被它包含的原始词之前，
    /// 否则「取消冻结」会先命中「冻结」而把恢复误判成冻结。
    /// </summary>
    private static readonly (CardStatusAction Action, string[] Phrases)[] PhraseTable =
    [
        (CardStatusAction.Unfreeze,
        [
            "取消冻结", "撤销冻结", "解冻", "解锁", "解除冻结", "解限制", "恢复使用", "恢复交易"
        ]),
        (CardStatusAction.UndoLost,
        [
            "取消挂失", "撤销挂失", "解除挂失", "解挂", "找回来了", "找到了卡", "卡找回了", "恢复挂失"
        ]),
        (CardStatusAction.Freeze,
        [
            "冻结", "冻了", "冻住", "止付", "被锁", "锁住", "锁了", "暂停使用"
        ]),
        (CardStatusAction.ReportLost,
        [
            "挂失", "报失", "丢了", "遗失", "不见了", "找不到", "被偷", "丢了钱包", "掉了"
        ]),
        (CardStatusAction.AdjustLimit,
        [
            "限额", "额度"
        ]),
    ];

    /// <summary>把用户原话解析成卡片操作诉求。</summary>
    /// <param name="userInput">用户原话。</param>
    /// <returns>识别到的操作诉求；没有写操作诉求时返回 <see cref="CardStatusAction.None"/>。</returns>
    public static CardStatusAction Resolve(string? userInput)
    {
        if (string.IsNullOrWhiteSpace(userInput)) return CardStatusAction.None;

        foreach (var (action, phrases) in PhraseTable)
        {
            if (phrases.Any(phrase => HasActiveOccurrence(userInput, phrase)))
            {
                return action;
            }
        }

        return CardStatusAction.None;
    }

    /// <summary>把操作诉求翻译成核心系统接受的状态文本。</summary>
    /// <param name="action">操作诉求。</param>
    /// <returns>核心系统状态文本；无对应状态时返回 null。</returns>
    public static string? ToCoreStatusText(CardStatusAction action) => action switch
    {
        CardStatusAction.ReportLost => "挂失",
        CardStatusAction.UndoLost => "解挂",
        CardStatusAction.Freeze => "冻结",
        CardStatusAction.Unfreeze => "解冻",
        _ => null
    };

    /// <summary>把操作诉求翻译成给用户看的中文描述。</summary>
    /// <param name="action">操作诉求。</param>
    /// <returns>中文描述。</returns>
    public static string Describe(CardStatusAction action) => action switch
    {
        CardStatusAction.ReportLost => "挂失",
        CardStatusAction.UndoLost => "解挂（撤销挂失）",
        CardStatusAction.Freeze => "冻结",
        CardStatusAction.Unfreeze => "解冻",
        CardStatusAction.AdjustLimit => "调整单日限额",
        _ => "查询"
    };

    /// <summary>
    /// 判断关键词在原文中是否存在**未被否定**的出现位置。
    /// 只要有一处出现没有被否定，就算命中。
    /// </summary>
    /// <param name="text">用户原话。</param>
    /// <param name="keyword">关键词。</param>
    /// <returns>存在有效命中返回 true。</returns>
    private static bool HasActiveOccurrence(string text, string keyword)
    {
        var searchFrom = 0;
        while (searchFrom <= text.Length - keyword.Length)
        {
            var index = text.IndexOf(keyword, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;

            if (!IsNegated(text, index)) return true;
            searchFrom = index + 1;
        }

        return false;
    }

    /// <summary>判断关键词出现位置前是否紧邻否定词。</summary>
    /// <param name="text">用户原话。</param>
    /// <param name="keywordIndex">关键词的起始位置。</param>
    /// <returns>被否定返回 true。</returns>
    private static bool IsNegated(string text, int keywordIndex)
    {
        var windowStart = Math.Max(0, keywordIndex - 3);
        if (windowStart >= keywordIndex) return false;

        var window = text[windowStart..keywordIndex];
        return NegationMarkers.Any(marker => window.Contains(marker, StringComparison.Ordinal));
    }
}
