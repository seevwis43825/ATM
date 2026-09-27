// ===== 端到端联调测试 =====
// 用法: cd src; dotnet run --project E2ETest
// 中文全部用 \uXXXX 转义，彻底规避 PowerShell/控制台编码问题。

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

var hostBase = args.Length > 0 ? args[0] : "http://localhost:5243";
var bankBase = args.Length > 1 ? args[1] : "http://localhost:5200";

var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var jsonOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
int passed = 0, failed = 0;

// 认证令牌：所有受保护端点必须携带 JWT
string token = "";

async Task<string> GetTokenAsync(string userId = "u_demo01", string password = "demo1234")
{
    var content = new StringContent(
        JsonSerializer.Serialize(new { userId, password }, jsonOpts),
        Encoding.UTF8, "application/json");
    var resp = await http.PostAsync($"{hostBase}/api/auth/token", content);
    if (!resp.IsSuccessStatusCode) return "";
    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    return doc.RootElement.GetProperty("token").GetString() ?? "";
}

void Section(string title)
{
    Console.WriteLine();
    Console.WriteLine(new string('=', 70));
    Console.WriteLine(" " + title);
    Console.WriteLine(new string('=', 70));
}

void Check(string name, bool ok, string detail = "")
{
    if (ok) { passed++; Console.WriteLine($"  [PASS] {name} {detail}"); }
    else { failed++; Console.WriteLine($"  [FAIL] {name} {detail}"); }
}

async Task<JsonElement> PostChat(string message, string userId, string? sessionId = null,
    Dictionary<string, object?>? slots = null)
{
    var body = new Dictionary<string, object?>
    {
        ["message"] = message,
        ["userId"] = userId
    };
    if (sessionId is not null) body["sessionId"] = sessionId;
    if (slots is not null) body["slots"] = slots;

    // Authorization 是请求头，必须挂在 HttpRequestMessage 上，不能加到 StringContent
    var request = new HttpRequestMessage(HttpMethod.Post, $"{hostBase}/api/chat")
    {
        Content = new StringContent(
            JsonSerializer.Serialize(body, jsonOpts), Encoding.UTF8, "application/json")
    };
    request.Headers.Add("Authorization", $"Bearer {token}");

    var resp = await http.SendAsync(request);
    var text = await resp.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(text);
    return doc.RootElement.Clone();
}

async Task<JsonElement> PostConfirm(string userId, string sessionId, decimal amount,
    Dictionary<string, object?> slots)
{
    var body = new Dictionary<string, object?>
    {
        ["userId"] = userId,
        ["sessionId"] = sessionId,
        ["amount"] = amount,
        ["slots"] = slots
    };
    var request = new HttpRequestMessage(HttpMethod.Post, $"{hostBase}/api/chat/confirm")
    {
        Content = new StringContent(
            JsonSerializer.Serialize(body, jsonOpts), Encoding.UTF8, "application/json")
    };
    request.Headers.Add("Authorization", $"Bearer {token}");

    var resp = await http.SendAsync(request);
    var text = await resp.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(text);
    return doc.RootElement.Clone();
}

string S(JsonElement e, string name) =>
    e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";
bool B(JsonElement e, string name) =>
    e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

// 中文转义
string CardQuery = "\u6211\u6709\u54ea\u4e9b\u5361";                    // 我有哪些卡
string BillQuery = "\u6211\u8fd9\u4e2a\u6708\u82b1\u4e86\u591a\u5c11\u94b1"; // 我这个月花了多少钱
string TransferCtor = "\u7ed9";                                       // 给

// ===== 前置检查 =====
Section("0. 环境连通性");
try
{
    var bankHealth = await http.GetStringAsync($"{bankBase}/health");
    Check("Mock Bank 在线", bankHealth.Contains("OK"), bankHealth.Length > 0 ? "" : "");
}
catch (Exception ex) { Check("Mock Bank 在线", false, ex.Message); return 1; }

try
{
    var hostHealth = await http.GetStringAsync($"{hostBase}/health");
    Check("AI Agent 宿主在线", hostHealth.Contains("healthy"));
}
catch (Exception ex) { Check("AI Agent 宿主在线", false, ex.Message); return 1; }

// ===== 场景 0B：鉴权 =====
Section("场景 0B: JWT 鉴权与越权防护");
{
    token = await GetTokenAsync();
    Check("可签发访问令牌", !string.IsNullOrEmpty(token));

    // 注意：Authorization 是请求头，必须加到 HttpRequestMessage 上，
    // 不能加到 StringContent（内容头）上。
    async Task<HttpResponseMessage> PostWithTokenAsync(
        string path, object body, string? bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{hostBase}{path}")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, jsonOpts), Encoding.UTF8, "application/json")
        };
        if (bearer is not null)
        {
            request.Headers.Add("Authorization", $"Bearer {bearer}");
        }
        return await http.SendAsync(request);
    }

    // 无令牌访问受保护端点
    try
    {
        var resp = await PostWithTokenAsync("/api/chat",
            new { message = "x", userId = "u_demo01" }, null);
        Check("无令牌访问被拒（401）", (int)resp.StatusCode == 401, $"HTTP {(int)resp.StatusCode}");
    }
    catch (Exception ex) { Check("无令牌访问被拒", false, ex.Message); }

    // 伪造令牌
    try
    {
        var resp = await PostWithTokenAsync("/api/chat",
            new { message = "x", userId = "u_demo01" }, "forged.token.value");
        Check("伪造令牌被拒（401）", (int)resp.StatusCode == 401, $"HTTP {(int)resp.StatusCode}");
    }
    catch (Exception ex) { Check("伪造令牌被拒", false, ex.Message); }

    // 越权：拿 demo01 的令牌查 demo02 的数据
    try
    {
        var resp = await PostWithTokenAsync("/api/chat",
            new { message = "\u6211\u6709\u54ea\u4e9b\u5361", userId = "u_demo02" }, token);
        var code = (int)resp.StatusCode;
        Check("越权访问他人数据被拒（401/403）", code is 401 or 403, $"HTTP {code}");
    }
    catch (Exception ex) { Check("越权访问被拒", false, ex.Message); }

    // 正确身份访问
    try
    {
        var resp = await PostWithTokenAsync("/api/chat",
            new { message = "\u6211\u6709\u54ea\u4e9b\u5361", userId = "u_demo01" }, token);
        Check("本人访问正常（200）", (int)resp.StatusCode == 200, $"HTTP {(int)resp.StatusCode}");
    }
    catch (Exception ex) { Check("本人访问正常", false, ex.Message); }

    // 普通用户不能管理插件
    try
    {
        var resp = await PostWithTokenAsync("/api/plugins/banking.card/stop",
            new { }, token);
        Check("普通用户停用插件被拒（403）", (int)resp.StatusCode == 403, $"HTTP {(int)resp.StatusCode}");
    }
    catch (Exception ex) { Check("普通用户停用插件被拒", false, ex.Message); }

    // 管理员可以
    try
    {
        var adminToken = await GetTokenAsync("admin_01", "admin1234");
        var resp = await PostWithTokenAsync("/api/plugins/banking.card/stop",
            new { }, adminToken);
        Check("管理员停用插件成功（200）", (int)resp.StatusCode == 200, $"HTTP {(int)resp.StatusCode}");

        // 立即恢复
        if ((int)resp.StatusCode == 200)
        {
            var restore = await PostWithTokenAsync("/api/plugins/banking.card/start",
                new { }, adminToken);
            Check("管理员启用插件成功（200）", (int)restore.StatusCode == 200, $"HTTP {(int)restore.StatusCode}");
        }
    }
    catch (Exception ex) { Check("管理员操作插件", false, ex.Message); }
}

// ===== 场景 1 =====
Section("场景 1: 查询银行卡 —— 验证 L3 数据强制脱敏 + 插件路由");
var r1 = await PostChat(CardQuery, "u_demo01");
Console.WriteLine("  回复: " + S(r1, "content"));
Check("意图路由到 card", S(r1, "intent").StartsWith("card"), "intent=" + S(r1, "intent"));
Check("执行成功", B(r1, "success"), S(r1, "errorMessage"));
var cardData = r1.TryGetProperty("data", out var cd) ? cd.GetRawText() : "{}";
Check("卡号已脱敏（不含完整 16 位卡号）", !System.Text.RegularExpressions.Regex.IsMatch(cardData, @"\d{13,19}"), "");
Console.WriteLine("  数据: " + cardData[..Math.Min(300, cardData.Length)]);

// ===== 场景 2 =====
Section("场景 2: 月度账单分析 —— 验证只读场景 + 脱敏");
var r2 = await PostChat(BillQuery, "u_demo01");
Console.WriteLine("  回复: " + S(r2, "content"));
Check("意图路由到 bill", S(r2, "intent").StartsWith("bill"), "intent=" + S(r2, "intent"));
Check("执行成功", B(r2, "success"), S(r2, "errorMessage"));
Check("账户号已脱敏", !r2.GetRawText().Contains("6222020200000001"), "");

// ===== 场景 3 =====
Section("场景 3: 小额转账 800 元 —— 低于阈值，应直接执行");
var balanceBefore = await http.GetStringAsync($"{bankBase}/api/corebank/v1/accounts/6222020200000001/balance");
var beforeBal = decimal.Parse(System.Text.RegularExpressions.Regex.Match(balanceBefore, @"""balance"":([\d.]+)").Groups[1].Value);
Console.WriteLine($"  转账前余额: {beforeBal:N2}");

var r3 = await PostChat(TransferCtor + "6222020200000003\u8f6c\u8d26500\u5143", "u_demo01", "e2e-small",
    new Dictionary<string, object?> { ["to_account"] = "6222020200000003", ["amount"] = 800 });
Console.WriteLine("  回复: " + S(r3, "content"));
Check("执行成功", B(r3, "success"), S(r3, "errorMessage"));
Check("不需人工确认", !B(r3, "requiresHumanInLoop"));
Check("副作用已提交", B(r3, "sideEffectCommitted"));
Check("返回流水号", S(r3, "data").Contains("TX"), "");

var balanceAfter = await http.GetStringAsync($"{bankBase}/api/corebank/v1/accounts/6222020200000001/balance");
var afterBal = decimal.Parse(System.Text.RegularExpressions.Regex.Match(balanceAfter, @"""balance"":([\d.]+)").Groups[1].Value);
Console.WriteLine($"  转账后余额: {afterBal:N2}");
Check("余额真实扣减 800 元", Math.Abs(beforeBal - afterBal - 800m) < 0.01m, $"差额 {beforeBal - afterBal:N2}");

// ===== 场景 4 =====
Section("场景 4: 大额转账 30000 元 —— 超阈值，应触发人工回环且不扣款");
var balBeforeLarge = afterBal;
var r4 = await PostChat("\u5927\u989d\u8f6c\u8d26", "u_demo01", "e2e-large",
    new Dictionary<string, object?> { ["to_account"] = "6222020200000003", ["amount"] = 30000 });
Console.WriteLine("  回复: " + S(r4, "content"));
Check("要求人工确认", B(r4, "requiresHumanInLoop"));
Check("未产生副作用", !B(r4, "sideEffectCommitted"));
Check("返回了规则 ID", S(r4, "data").Contains("rule_id"), "");

var balDuringPending = decimal.Parse(System.Text.RegularExpressions.Regex.Match(
    await http.GetStringAsync($"{bankBase}/api/corebank/v1/accounts/6222020200000001/balance"),
    @"""balance"":([\d.]+)").Groups[1].Value);
Check("等待确认期间余额未变", Math.Abs(balBeforeLarge - balDuringPending) < 0.01m, $"{balBeforeLarge:N2} -> {balDuringPending:N2}");

// ===== 场景 5 =====
Section("场景 5: 人工确认后执行 —— 验证 HITL 闭环");
var r5 = await PostConfirm("u_demo01", "e2e-large", 30000, new Dictionary<string, object?>
{
    ["to_account"] = "6222020200000003",
    ["amount"] = 30000,
    ["from_account"] = "6222020200000001"
});
Console.WriteLine("  回复: " + S(r5, "content"));
Check("执行成功", B(r5, "success"), S(r5, "errorMessage"));
Check("副作用已提交", B(r5, "committed"));

var balAfterLarge = decimal.Parse(System.Text.RegularExpressions.Regex.Match(
    await http.GetStringAsync($"{bankBase}/api/corebank/v1/accounts/6222020200000001/balance"),
    @"""balance"":([\d.]+)").Groups[1].Value);
Console.WriteLine($"  确认后余额: {balAfterLarge:N2}");
Check("确认后扣款 30000 元", Math.Abs(balDuringPending - balAfterLarge - 30000m) < 0.01m,
    $"差额 {balDuringPending - balAfterLarge:N2}");

// ===== 场景 6 =====
Section("场景 6: 合规拦截 —— 转给自己应被拒绝");
var r6 = await PostChat("\u8f6c\u8d26\u7ed9\u81ea\u5df1", "u_demo01", "e2e-self",
    new Dictionary<string, object?> { ["to_account"] = "6222020200000001", ["amount"] = 500 });
Console.WriteLine("  错误: " + S(r6, "errorCode") + " / " + S(r6, "errorMessage"));
Check("被合规规则拒绝", S(r6, "errorCode") == "COMPLIANCE_REJECTED", S(r6, "errorCode"));
Check("无副作用", !B(r6, "sideEffectCommitted"));

// ===== 场景 7 =====
Section("场景 7: 反洗钱阈值 —— 80000 元应要求人工复核");
var r7 = await PostChat("\u8f6c\u8d26 80000 \u5143", "u_demo01", "e2e-aml",
    new Dictionary<string, object?> { ["to_account"] = "6222020200000003", ["amount"] = 80000 });
Console.WriteLine("  回复: " + S(r7, "content"));
Check("要求人工复核", B(r7, "requiresHumanInLoop"));
Check("无副作用", !B(r7, "sideEffectCommitted"));

// ===== 场景 8 =====
Section("场景 8: 幂等性 —— 重复提交同一 sessionId 不应重复扣款");
var balBeforeDup = decimal.Parse(System.Text.RegularExpressions.Regex.Match(
    await http.GetStringAsync($"{bankBase}/api/corebank/v1/accounts/6222020200000001/balance"),
    @"""balance"":([\d.]+)").Groups[1].Value);
var r8 = await PostChat("\u91cd\u590d\u8f6c\u8d26", "u_demo01", "e2e-small",
    new Dictionary<string, object?> { ["to_account"] = "6222020200000003", ["amount"] = 800 });
Console.WriteLine("  回复: " + S(r8, "content"));
var balAfterDup = decimal.Parse(System.Text.RegularExpressions.Regex.Match(
    await http.GetStringAsync($"{bankBase}/api/corebank/v1/accounts/6222020200000001/balance"),
    @"""balance"":([\d.]+)").Groups[1].Value);
Check("余额未重复扣款", Math.Abs(balBeforeDup - balAfterDup) < 0.01m, $"{balBeforeDup:N2} -> {balAfterDup:N2}");

// ===== 场景 9 =====
Section("场景 9: 事件总线 —— 跨插件联动");
{
    var evReq = new HttpRequestMessage(HttpMethod.Get, $"{hostBase}/api/plugins/events");
    evReq.Headers.Add("Authorization", $"Bearer {token}");
    var evResp = await http.SendAsync(evReq);
    var evText = await evResp.Content.ReadAsStringAsync();

    using var evDoc = JsonDocument.Parse(evText);
    var published = evDoc.RootElement.GetProperty("published").GetInt32();
    Console.WriteLine($"  已发布事件数: {published}");
    Check("transfer.completed 事件已发布", evText.Contains("transfer.completed"), "");
    foreach (var e in evDoc.RootElement.GetProperty("recent").EnumerateArray())
    {
        Console.WriteLine($"    - {e.GetProperty("eventType").GetString()} from {e.GetProperty("source").GetString()}");
    }
}

// ===== 场景 10 =====
Section("场景 10: 审计链 —— 不可篡改 + 用户 ID 哈希");
var auditPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
    "src", "BankingAgent.Host", "logs", "audit-chain.log");
var found = Directory.GetFiles(Directory.GetCurrentDirectory(), "audit-chain.log", SearchOption.AllDirectories)
    .FirstOrDefault();
if (found is not null)
{
    var lines = await File.ReadAllLinesAsync(found);
    Console.WriteLine($"  审计记录数: {lines.Length}");
    Check("审计记录已生成", lines.Length > 0, $"({lines.Length} 条)");
    Check("用户 ID 已哈希（不出现明文 u_demo01）", !lines.Any(l => l.Contains("u_demo01")), "");
    var last = lines[^1];
    using (var d = JsonDocument.Parse(last))
    {
        var root = d.RootElement;
        Check("含链式签名 sig", root.TryGetProperty("sig", out _), "");
        Check("含前序签名 prev", root.TryGetProperty("prev", out _), "");
        Console.WriteLine("  末条: " + last[..Math.Min(220, last.Length)]);
    }
}
else
{
    Check("审计文件存在", false, "未找到 audit-chain.log");
}

// ===== 汇总 =====
Section("测试汇总");
Console.WriteLine($"  通过: {passed}    失败: {failed}");
Console.WriteLine(failed == 0 ? "\n  *** 全部通过 ***\n" : $"\n  *** {failed} 项失败 ***\n");
return failed == 0 ? 0 : 1;
