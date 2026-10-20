// ===== 敏感文本脱敏 =====
//
// 送模型之前，所有用户输入都必须先过这里。
// 抽成独立组件的原因：意图分类器与连通性探针都要用同一套脱敏规则，
// 否则探针「测通了」而真实链路没脱敏，就是自欺欺人。
//
// 数据边界：账号、手机号、身份证不得离开系统边界，
// 而意图识别并不需要这些原文 —— 保留后 4 位足以让人核对，不构成完整泄漏。

namespace BankingAgent.Base.Ai;

using System.Text.RegularExpressions;
using BankingAgent.Base.Security;

/// <summary>对送模型的文本做强制脱敏与长度截断。</summary>
public static class SensitiveTextMasker
{
    /// <summary>18 位身份证（先于账号匹配，避免被长数字规则误伤）。</summary>
    private static readonly Regex IdCardDigits = new(@"(?<!\d)\d{18}(?!\d)", RegexOptions.Compiled);

    /// <summary>11 位手机号。</summary>
    private static readonly Regex MobileDigits = new(@"(?<!\d)1[3-9]\d{9}(?!\d)", RegexOptions.Compiled);

    /// <summary>12-19 位账号 / 卡号。</summary>
    private static readonly Regex AccountDigits = new(@"(?<!\d)\d{12,19}(?!\d)", RegexOptions.Compiled);

    /// <summary>对文本做脱敏，并在超过上限时截断。</summary>
    /// <param name="input">原始文本。</param>
    /// <param name="maxChars">最大长度；超出部分被截断。</param>
    /// <returns>脱敏后的文本。</returns>
    public static string Mask(string input, int maxChars)
    {
        if (string.IsNullOrEmpty(input)) return input;

        var masked = IdCardDigits.Replace(input, m => DataMasker.MaskIdCard(m.Value));
        masked = MobileDigits.Replace(masked, m => DataMasker.MaskPhone(m.Value));
        masked = AccountDigits.Replace(masked, m => DataMasker.MaskAccount(m.Value));

        return masked.Length <= maxChars ? masked : masked[..maxChars];
    }
}
