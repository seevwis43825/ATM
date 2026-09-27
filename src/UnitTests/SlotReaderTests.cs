// ===== 槽位解析单元测试 =====
// 槽位来自 JSON 反序列化，类型不匹配会导致业务逻辑静默失效
// （例如金额读成 null → 走到"金额缺失"分支，而非真正校验金额）。
// 这是本项目实际踩过的坑，用测试锁死。

using System.Text.Json;
using BankingAgent.Base.Agents;
using BankingAgent.PluginSdk;

namespace UnitTests;

public class SlotReaderTests
{
    /// <summary>
    /// 模拟 ASP.NET Core 把请求体反序列化成 object 的行为。
    /// 注意：JsonElement 的生命周期依附于 JsonDocument，
    /// 因此必须把文档保留在列表中，否则访问时会抛 ObjectDisposedException
    /// —— 这也是 SlotReader 做防御性处理的原因。
    /// </summary>
    private static readonly List<JsonDocument> KeepAlive = [];

    private static AgentRequest RequestFromJson(string json)
    {
        var doc = JsonDocument.Parse(json);
        KeepAlive.Add(doc);
        var slots = doc.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => (object?)p.Value);
        return new AgentRequest { UserInput = "test", UserId = "u_1", Slots = slots };
    }

    // ===== 字符串槽位 =====

    [Fact]
    public void String_FromJsonString_Works()
    {
        var req = RequestFromJson("""{"to_account":"6222020200000001"}""");
        Assert.Equal("6222020200000001", SlotReader.String(req, "to_account"));
    }

    [Fact]
    public void String_FromPlainString_Works()
    {
        var req = new AgentRequest
        {
            UserInput = "t", UserId = "u",
            Slots = new Dictionary<string, object?> { ["k"] = "value" }
        };
        Assert.Equal("value", SlotReader.String(req, "k"));
    }

    [Fact]
    public void String_FromJsonNumber_ReturnsText()
    {
        var req = RequestFromJson("""{"card_no":6222020200000001}""");
        Assert.Equal("6222020200000001", SlotReader.String(req, "card_no"));
    }

    [Fact]
    public void String_MissingKey_ReturnsNull()
    {
        var req = RequestFromJson("""{"other":"x"}""");
        Assert.Null(SlotReader.String(req, "to_account"));
    }

    // ===== 金额槽位（本项目实际踩过的坑）=====

    [Fact]
    public void Decimal_FromJsonNumber_Works()
    {
        var req = RequestFromJson("""{"amount":30000}""");
        Assert.Equal(30000m, SlotReader.Decimal(req, "amount"));
    }

    [Fact]
    public void Decimal_FromJsonDecimal_Works()
    {
        var req = RequestFromJson("""{"amount":1234.56}""");
        Assert.Equal(1234.56m, SlotReader.Decimal(req, "amount"));
    }

    [Fact]
    public void Decimal_FromJsonString_Works()
    {
        var req = RequestFromJson("""{"amount":"5000.50"}""");
        Assert.Equal(5000.50m, SlotReader.Decimal(req, "amount"));
    }

    [Fact]
    public void Decimal_FromPlainDecimal_Works()
    {
        var req = new AgentRequest
        {
            UserInput = "t", UserId = "u",
            Slots = new Dictionary<string, object?> { ["amount"] = 888.88m }
        };
        Assert.Equal(888.88m, SlotReader.Decimal(req, "amount"));
    }

    [Fact]
    public void Decimal_FromInt_Works()
    {
        var req = new AgentRequest
        {
            UserInput = "t", UserId = "u",
            Slots = new Dictionary<string, object?> { ["amount"] = 800 }
        };
        Assert.Equal(800m, SlotReader.Decimal(req, "amount"));
    }

    [Fact]
    public void Decimal_NonNumericString_ReturnsNull()
    {
        var req = RequestFromJson("""{"amount":"abc"}""");
        Assert.Null(SlotReader.Decimal(req, "amount"));
    }

    [Fact]
    public void Decimal_MissingKey_ReturnsNull()
    {
        var req = RequestFromJson("""{}""");
        Assert.Null(SlotReader.Decimal(req, "amount"));
    }

    [Fact]
    public void Decimal_NullValue_ReturnsNull()
    {
        var req = RequestFromJson("""{"amount":null}""");
        Assert.Null(SlotReader.Decimal(req, "amount"));
    }

    [Fact]
    public void Decimal_ZeroAmount_IsPreserved()
    {
        // 0 是有效值，不能被当成 null —— 金额 0 必须在合规层被拒绝
        var req = RequestFromJson("""{"amount":0}""");
        Assert.Equal(0m, SlotReader.Decimal(req, "amount"));
    }

    [Fact]
    public void Decimal_NegativeAmount_IsPreserved()
    {
        // 负数也必须原样透传给合规层判断，不能在这里被吞掉
        var req = RequestFromJson("""{"amount":-100}""");
        Assert.Equal(-100m, SlotReader.Decimal(req, "amount"));
    }

    // ===== 整数槽位 =====

    [Fact]
    public void Int_FromJsonNumber_Works()
    {
        var req = RequestFromJson("""{"year":2026,"month":9}""");
        Assert.Equal(2026, SlotReader.Int(req, "year"));
        Assert.Equal(9, SlotReader.Int(req, "month"));
    }

    [Fact]
    public void Int_FromJsonString_Works()
    {
        var req = RequestFromJson("""{"year":"2026"}""");
        Assert.Equal(2026, SlotReader.Int(req, "year"));
    }

    // ===== 布尔槽位 =====

    [Fact]
    public void Bool_FromJsonTrue_Works()
    {
        var req = RequestFromJson("""{"confirmed":true}""");
        Assert.True(SlotReader.Bool(req, "confirmed"));
    }

    [Fact]
    public void Bool_FromJsonFalse_Works()
    {
        var req = RequestFromJson("""{"confirmed":false}""");
        Assert.False(SlotReader.Bool(req, "confirmed"));
    }

    [Fact]
    public void Bool_FromJsonString_Works()
    {
        var req = RequestFromJson("""{"confirmed":"true"}""");
        Assert.True(SlotReader.Bool(req, "confirmed"));
    }

    [Fact]
    public void Bool_MissingKey_ReturnsNull()
    {
        var req = RequestFromJson("""{}""");
        Assert.Null(SlotReader.Bool(req, "confirmed"));
    }

    // ===== 综合场景 =====

    [Fact]
    public void FullTransferRequest_AllSlotsParsed()
    {
        // 复现真实转账请求
        var req = RequestFromJson("""
        {
          "to_account": "6222020200000003",
          "amount": 30000,
          "from_account": "6222020200000001",
          "confirmed": true
        }
        """);

        Assert.Equal("6222020200000003", SlotReader.String(req, "to_account"));
        Assert.Equal(30000m, SlotReader.Decimal(req, "amount"));
        Assert.Equal("6222020200000001", SlotReader.String(req, "from_account"));
        Assert.True(SlotReader.Bool(req, "confirmed"));
    }

    [Fact]
    public void EmptySlots_DoNotThrow()
    {
        var req = new AgentRequest { UserInput = "t", UserId = "u" };
        Assert.Null(SlotReader.String(req, "x"));
        Assert.Null(SlotReader.Decimal(req, "x"));
        Assert.Null(SlotReader.Int(req, "x"));
        Assert.Null(SlotReader.Bool(req, "x"));
    }
}
