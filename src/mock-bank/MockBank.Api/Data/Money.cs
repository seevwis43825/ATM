// ===== 金额值对象：统一使用 decimal 承载金额，杜绝 double/float 引入精度误差 =====

using System.Globalization;

namespace MockBank.Api.Data;

/// <summary>
/// 金额值对象。内部只使用 <see cref="decimal"/> 存储金额，币种仅作为展示与校验标签。
/// </summary>
/// <param name="Amount">金额数值（单位：元）。</param>
/// <param name="Currency">币种代码，默认 CNY。</param>
public readonly record struct Money(decimal Amount, string Currency = "CNY")
{
    /// <summary>零金额。</summary>
    public static Money Zero => new(0m);

    /// <summary>构造一个人民币金额。</summary>
    /// <param name="amount">金额数值。</param>
    /// <returns>币种为 CNY 的 <see cref="Money"/>。</returns>
    public static Money Cny(decimal amount) => new(amount, "CNY");

    /// <summary>金额是否为正数。</summary>
    public bool IsPositive => Amount > 0m;

    /// <summary>金额是否为零。</summary>
    public bool IsZero => Amount == 0m;

    /// <summary>按银行四舍五入（ AwayFromZero）保留两位小数。</summary>
    /// <returns>保留两位小数的金额。</returns>
    public Money Round2() => new(Math.Round(Amount, 2, MidpointRounding.AwayFromZero), Currency);

    /// <summary>金额加法。</summary>
    /// <param name="left">左操作数。</param>
    /// <param name="right">右操作数。</param>
    /// <returns>两者之和。</returns>
    public static Money operator +(Money left, Money right) => new(left.Amount + right.Amount, left.Currency);

    /// <summary>金额减法。</summary>
    /// <param name="left">左操作数。</param>
    /// <param name="right">右操作数。</param>
    /// <returns>两者之差。</returns>
    public static Money operator -(Money left, Money right) => new(left.Amount - right.Amount, left.Currency);

    /// <summary>金额取负。</summary>
    /// <param name="value">操作数。</param>
    /// <returns>相反数。</returns>
    public static Money operator -(Money value) => new(-value.Amount, value.Currency);

    /// <summary>金额大于比较。</summary>
    /// <param name="left">左操作数。</param>
    /// <param name="right">右操作数。</param>
    /// <returns>左大于右时为 true。</returns>
    public static bool operator >(Money left, Money right) => left.Amount > right.Amount;

    /// <summary>金额小于比较。</summary>
    /// <param name="left">左操作数。</param>
    /// <param name="right">右操作数。</param>
    /// <returns>左小于右时为 true。</returns>
    public static bool operator <(Money left, Money right) => left.Amount < right.Amount;

    /// <summary>金额大于等于比较。</summary>
    /// <param name="left">左操作数。</param>
    /// <param name="right">右操作数。</param>
    /// <returns>左大于等于右时为 true。</returns>
    public static bool operator >=(Money left, Money right) => left.Amount >= right.Amount;

    /// <summary>金额小于等于比较。</summary>
    /// <param name="left">左操作数。</param>
    /// <param name="right">右操作数。</param>
    /// <returns>左小于等于右时为 true。</returns>
    public static bool operator <=(Money left, Money right) => left.Amount <= right.Amount;

    /// <summary>隐式转换为原始 decimal，便于与存储层交互。</summary>
    /// <param name="money">金额对象。</param>
    /// <returns>金额数值。</returns>
    public static implicit operator decimal(Money money) => money.Amount;

    /// <summary>由 decimal 显式构造金额对象。</summary>
    /// <param name="amount">金额数值。</param>
    /// <returns>币种为 CNY 的 <see cref="Money"/>。</returns>
    public static explicit operator Money(decimal amount) => new(amount);

    /// <summary>格式化为“1234.56 CNY”。</summary>
    /// <returns>可读金额字符串。</returns>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Amount:F2} {Currency}");
}
