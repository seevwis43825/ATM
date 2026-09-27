// ===== 数据脱敏单元测试 =====
// 脱敏是不可回退的安全边界：一旦明文泄漏就不可挽回。
// 每个掩码格式都必须被测试锁定。

using BankingAgent.Base.Security;
using BankingAgent.PluginSdk;

namespace UnitTests;

public class DataMaskerTests
{
    [Fact]
    public void MaskPhone_KeepsFirst3AndLast4()
    {
        Assert.Equal("138****8000", DataMasker.MaskPhone("13800138000"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("abcdefghijk")]
    public void MaskPhone_InvalidInput_AlwaysMasked(string? input)
    {
        Assert.Equal("****", DataMasker.MaskPhone(input));
    }

    [Fact]
    public void MaskPhone_WhitespaceInput_Masked()
    {
        Assert.Equal("****", DataMasker.MaskPhone("   "));
    }

    [Fact]
    public void MaskIdCard_KeepsFirst6AndLast4()
    {
        Assert.Equal("110101********1234", DataMasker.MaskIdCard("110101199001011234"));
    }

    [Fact]
    public void MaskIdCard_HandlesLowercaseX()
    {
        var result = DataMasker.MaskIdCard("11010119900101123x");
        Assert.Equal("110101********123x", result);
    }

    [Fact]
    public void MaskBankCard_KeepsOnlyLast4()
    {
        var result = DataMasker.MaskBankCard("6222020200000001");
        Assert.Equal("**** **** **** 0001", result);
        Assert.DoesNotContain("622202020000", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12")]
    [InlineData("abc")]
    public void MaskBankCard_ShortOrInvalid_Masked(string? input)
    {
        Assert.Equal("****", DataMasker.MaskBankCard(input));
    }

    [Fact]
    public void MaskName_SingleChar_Unchanged()
    {
        Assert.Equal("张", DataMasker.MaskName("张"));
    }

    [Fact]
    public void MaskName_TwoChars_FirstVisible()
    {
        Assert.Equal("张*", DataMasker.MaskName("张明"));
    }

    [Fact]
    public void MaskName_LongName_OnlyFirstVisible()
    {
        var result = DataMasker.MaskName("欧阳明日");
        Assert.Equal("欧***", result);
        Assert.DoesNotContain("阳", result);
    }

    [Fact]
    public void MaskEmail_KeepsFirstCharAndDomain()
    {
        Assert.Equal("z***@example.com", DataMasker.MaskEmail("zhang@example.com"));
    }

    [Fact]
    public void MaskAccount_KeepsLast4()
    {
        var result = DataMasker.MaskAccount("6222020200000001");
        Assert.EndsWith("0001", result);
        Assert.Equal(16, result.Length);
    }

    // ===== 按敏感级别 =====

    [Fact]
    public void Apply_L1_NoMasking()
    {
        Assert.Equal("6222020200000001",
            DataMasker.Apply("6222020200000001", DataClassification.L1, "card"));
    }

    [Fact]
    public void Apply_L4_NeverReturnsPlaintext()
    {
        var secret = "123456";
        var result = DataMasker.Apply(secret, DataClassification.L4, "password");
        Assert.Equal("********", result);
        Assert.DoesNotContain(secret, result);
    }

    [Fact]
    public void Apply_L2_UsesFieldSpecificMasking()
    {
        Assert.Equal("138****8000",
            DataMasker.Apply("13800138000", DataClassification.L2, "phone"));
    }

    [Fact]
    public void Apply_L3_UsesFieldSpecificMasking()
    {
        Assert.Equal("110101********1234",
            DataMasker.Apply("110101199001011234", DataClassification.L3, "idcard"));
    }

    [Fact]
    public void Apply_UnknownField_FallsBackToAccountMasking()
    {
        var result = DataMasker.Apply("ABCDEFGHIJKLMNOP", DataClassification.L3, "unknownfield");
        Assert.Equal("************MNOP", result);
    }

    // ===== 对象图脱敏 =====

    [Fact]
    public void MaskGraph_HandlesDictionary()
    {
        var input = new Dictionary<string, object?>
        {
            ["phone"] = "13800138000",
            ["name"] = "张明",
            ["amount"] = 5000m
        };

        var result = DataMasker.MaskGraph(input, DataClassification.L3);

        var map = Assert.IsType<Dictionary<string, object?>>(result);
        Assert.Equal("138****8000", map["phone"]);
        Assert.Equal("张*", map["name"]);
        // 数值类型保持原值，不做脱敏
        Assert.Equal(5000m, Convert.ToDecimal(map["amount"]));
    }

    [Fact]
    public void MaskGraph_L4_MasksWholeGraph()
    {
        var input = new Dictionary<string, object?> { ["secret"] = "abc" };
        var result = DataMasker.MaskGraph(input, DataClassification.L4);
        Assert.Equal("********", result);
    }

    [Fact]
    public void MaskGraph_HandlesCollection()
    {
        // 集合元素没有字段名，只能走通用掩码（保留末 4 位）
        var input = new List<object?> { "13800138000", 100 };
        var result = DataMasker.MaskGraph(input, DataClassification.L3);

        var list = Assert.IsType<List<object?>>(result);
        Assert.Equal("*******8000", list[0]);
        Assert.Equal(100, list[1]);
    }

    [Fact]
    public void MaskGraph_Dictionary_UsesKeyAwareMasking()
    {
        // 字典有键名，应按字段类型脱敏而非通用掩码
        var input = new Dictionary<string, object?> { ["phone"] = "13800138000" };
        var result = DataMasker.MaskGraph(input, DataClassification.L3);

        var map = Assert.IsType<Dictionary<string, object?>>(result);
        Assert.Equal("138****8000", map["phone"]);
    }

    [Fact]
    public void MaskGraph_Null_ReturnsNull()
    {
        Assert.Null(DataMasker.MaskGraph(null, DataClassification.L3));
    }

    // ===== 泄露防护：核心断言 =====

    [Fact]
    public void SecurityInvariant_NoPlaintextLeakage()
    {
        // 关键回归测试：任何 L3/L4 字段经脱敏后都不得包含原文片段
        var cases = new (string Value, string Kind)[]
        {
            ("13800138000", "phone"),
            ("110101199001011234", "idcard"),
            ("6222020200000001", "card"),
            ("zhangsan@example.com", "email")
        };

        foreach (var (value, kind) in cases)
        {
            foreach (var level in new[]
                     {
                         DataClassification.L2, DataClassification.L3, DataClassification.L4
                     })
            {
                var masked = DataMasker.Apply(value, level, kind);
                if (level == DataClassification.L4)
                {
                    Assert.Equal("********", masked);
                }
                else
                {
                    // 至少不能完整包含原文
                    Assert.False(masked.Contains(value),
                        $"{kind} 在 {level} 级别下泄漏了原文");
                }
            }
        }
    }
}
