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

// sessionId 同时充当转账幂等键，且会落库持久化。
// 如果用固定的 "e2e-small"，第二次运行就会命中幂等拦截，
// 转账被跳过 → 余额断言、事件断言连锁失败。
// 因此每次运行生成唯一前缀；而"幂等性"场景改为复用本次运行的同一个 ID，
// 语义与真实客户端重试完全一致。
var runId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
string Sess(string name) => $"e2e-{runId}-{name}";

// 认证令牌：所有受保护端点必须携带 JWT
string token = "";

async Task<string> GetTokenAsync(string userId = "u_demo01", string password = "demo1234")
{
    // /api/auth/token 有令牌桶限流（默认 5 次/分钟），而本套件需要为多个
    // 角色各取一次令牌，连续运行时很容易撞上限流。
    // 限流本身是必须保留的安全能力，所以这里做退避重试而不是放宽阈值。
    for (var attempt = 0; attempt < 8; attempt++)
    {
        var content = new StringContent(
            JsonSerializer.Serialize(new { userId, password }, jsonOpts),
            Encoding.UTF8, "application/json");
        var resp = await http.PostAsync($"{hostBase}/api/auth/token", content);

        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // 必须尊重 Retry-After：令牌桶按 AuthPermitsPerMinute/BurstCapacity 匀速补充，
            // 典型值是 12 秒一个令牌。早于该时间重试只会再拿到 429。
            var wait = resp.Headers.RetryAfter?.Delta
                       ?? (resp.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)
                       ?? TimeSpan.FromSeconds(12);
            if (wait < TimeSpan.FromSeconds(1)) wait = TimeSpan.FromSeconds(1);
            if (wait > TimeSpan.FromSeconds(60)) wait = TimeSpan.FromSeconds(60);

            Console.WriteLine($"  [限流] 取令牌被拒（429），{wait.TotalSeconds:F0}s 后重试…");
            await Task.Delay(wait);
            continue;
        }

        if (!resp.IsSuccessStatusCode) return "";
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("token").GetString() ?? "";
    }

    Console.WriteLine("  [限流] 取令牌重试次数耗尽，仍被限流");
    return "";
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

// 读取审计链文件。
// 宿主默认让审计文件保持常驻写句柄（Audit:KeepFileOpen），而标准
// File.ReadAllLines 用 FileShare.Read 打开 —— 与写句柄共享模式冲突，
// 会直接抛 IOException。合规读取方必须用 FileShare.ReadWrite。
async Task<string[]> ReadAuditLinesAsync(string path)
{
    using var fs = new FileStream(
        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var sr = new StreamReader(fs);
    var all = await sr.ReadToEndAsync();
    return all.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
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

// ===== 前置：重置演示数据 =====
//
// 为什么必须自动做：场景 3/4/5/8 都对账户余额做**精确断言**（扣 800、扣 30000）。
// 上一轮 E2E 已经把钱扣走了，第二轮再跑同一套数据余额就不够 ——
// 表现为「场景 5 因余额不足失败」，而真正的根因（前置数据没重置）离失败点很远，排查成本很高。
// 以前这靠人记得手工 POST 一次 _admin/reset，现在由脚本自己做。
//
// 这一步同时是**回归护栏**：该端点曾因 SQLite 连接池没释放而直接返回 500
// （数据库文件删不掉），而它当时是「人肉前置条件」—— 不执行就没人发现，CI 也不会红。
// 现在 CI 每跑一次 E2E 就会真的打它一次。
Section("0A. 重置演示数据");
try
{
    var resetResp = await http.PostAsync($"{bankBase}/api/corebank/v1/_admin/reset", null);
    var resetBody = await resetResp.Content.ReadAsStringAsync();
    if (!resetResp.IsSuccessStatusCode)
    {
        Check("Mock Bank 演示数据已重置", false, $"HTTP {(int)resetResp.StatusCode}: {resetBody}");
        Console.WriteLine("  重置失败：后续余额断言将失去意义，终止 E2E。");
        return 1;
    }

    Check("Mock Bank 演示数据已重置", true);
    Console.WriteLine("  响应: " + resetBody);
}
catch (Exception ex)
{
    Check("Mock Bank 演示数据已重置", false, ex.Message);
    Console.WriteLine("  重置失败：后续余额断言将失去意义，终止 E2E。");
    return 1;
}

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
        // 断言的是「未认证请求必须被拒绝」，不是「必须返回 401」。
        // 中间件顺序是 限流 → 认证，因此高频连跑时 IP 维度限流会先返回 429。
        // 两者都是合法的拒绝，把 429 算作失败会造成假红。
        Check("无令牌访问被拒（401/429）", (int)resp.StatusCode is 401 or 429, $"HTTP {(int)resp.StatusCode}");
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

// 小额转账的 sessionId 要留给场景 8 复用（验证幂等）
var smallSession = Sess("small");

// 显式指定付款账户：多银行之后「默认第一个账户」会随卡号段排序落到建行，
// 这里固定用工行账户，让断言的余额对象明确、不随数据变化漂移。
var r3 = await PostChat(TransferCtor + "6222020200000003\u8f6c\u8d26500\u5143", "u_demo01", smallSession,
    new Dictionary<string, object?>
    {
        ["to_account"] = "6222020200000003",
        ["from_account"] = "6222020200000001",
        ["amount"] = 800
    });
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
// 场景 5 的确认必须带同一个 sessionId，才能续上这笔待审批转账
var largeSession = Sess("large");
var r4 = await PostChat("\u5927\u989d\u8f6c\u8d26", "u_demo01", largeSession,
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
var r5 = await PostConfirm("u_demo01", largeSession, 30000, new Dictionary<string, object?>
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
var r6 = await PostChat("\u8f6c\u8d26\u7ed9\u81ea\u5df1", "u_demo01", Sess("self"),
    new Dictionary<string, object?>
    {
        ["to_account"] = "6222020200000001",
        ["from_account"] = "6222020200000001",
        ["amount"] = 500
    });
Console.WriteLine("  错误: " + S(r6, "errorCode") + " / " + S(r6, "errorMessage"));
Check("被合规规则拒绝", S(r6, "errorCode") == "COMPLIANCE_REJECTED", S(r6, "errorCode"));
Check("无副作用", !B(r6, "sideEffectCommitted"));

// ===== 场景 7 =====
Section("场景 7: 反洗钱阈值 —— 80000 元应要求人工复核");
var r7 = await PostChat("\u8f6c\u8d26 80000 \u5143", "u_demo01", Sess("aml"),
    new Dictionary<string, object?> { ["to_account"] = "6222020200000003", ["amount"] = 80000 });
Console.WriteLine("  回复: " + S(r7, "content"));
Check("要求人工复核", B(r7, "requiresHumanInLoop"));
Check("无副作用", !B(r7, "sideEffectCommitted"));

// ===== 场景 8 =====
Section("场景 8: 幂等性 —— 重复提交同一 sessionId 不应重复扣款");
var balBeforeDup = decimal.Parse(System.Text.RegularExpressions.Regex.Match(
    await http.GetStringAsync($"{bankBase}/api/corebank/v1/accounts/6222020200000001/balance"),
    @"""balance"":([\d.]+)").Groups[1].Value);
var r8 = await PostChat("\u91cd\u590d\u8f6c\u8d26", "u_demo01", smallSession,
    new Dictionary<string, object?>
    {
        ["to_account"] = "6222020200000003",
        ["from_account"] = "6222020200000001",
        ["amount"] = 800
    });
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
    // 审计文件默认保持常驻写句柄（Audit:KeepFileOpen），标准 File.ReadAllLines
    // 用 FileShare.Read 打开会与写句柄冲突 → 必须用 FileShare.ReadWrite。
    var lines = await ReadAuditLinesAsync(found);
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

// ===== 场景 11：跨行卡片 =====
Section("场景 11: 跨行卡片 —— 一个客户在多家银行的卡应一次返回");
{
    // 直接查核心系统，确认卡片确实来自多个独立的银行数据库。
    var banksJson = await http.GetStringAsync($"{bankBase}/api/corebank/v1/banks");
    using var banksDoc = JsonDocument.Parse(banksJson);
    var bankCount = banksDoc.RootElement.GetArrayLength();
    Check("模拟银行目录含 3 家银行", bankCount == 3, $"banks={bankCount}");

    // 每家银行一个独立数据库文件
    var dbFiles = banksDoc.RootElement.EnumerateArray()
        .Select(b => b.GetProperty("bank").GetProperty("databaseFileName").GetString() ?? "")
        .ToList();
    Check("每家银行有独立数据库文件", dbFiles.Distinct().Count() == dbFiles.Count,
        string.Join(", ", dbFiles));

    var cardsJson = await http.GetStringAsync($"{bankBase}/api/corebank/v1/cards?userId=u_demo01");
    using var cardsDoc = JsonDocument.Parse(cardsJson);
    var cardBanks = cardsDoc.RootElement.EnumerateArray()
        .Select(c => c.GetProperty("bankCode").GetString() ?? "")
        .Distinct().ToList();
    Check("演示用户持有多个银行的卡", cardBanks.Count >= 3, string.Join(",", cardBanks));

    // 通过对话入口同样能看到跨行卡片
    var r11 = await PostChat(CardQuery, "u_demo01");
    var r11Text = r11.GetRawText();
    Check("对话返回包含发卡行信息", r11Text.Contains("bankCode") || r11Text.Contains("bank_name"),
        S(r11, "content"));
}

// ===== 场景 12：跨行转账 =====
Section("场景 12: 跨行转账 —— 付款行与收款行不同");
{
    // 建行账户 -> 工行账户（李华），验证双边记账
    var body = new Dictionary<string, object?>
    {
        ["fromAccountNo"] = "6217000100000001",
        ["toAccountNo"] = "6222020200000003",
        ["amount"] = 123.45m,
        ["remark"] = "e2e interbank"
    };
    var resp = await http.PostAsync($"{bankBase}/api/corebank/v1/transfers",
        new StringContent(JsonSerializer.Serialize(body, jsonOpts), Encoding.UTF8, "application/json"));
    var text = await resp.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(text);

    Check("跨行转账成功", (int)resp.StatusCode == 200, $"HTTP {(int)resp.StatusCode}");
    Check("标记为跨行", doc.RootElement.GetProperty("isInterBank").GetBoolean());
    Check("返回双边流水号",
        doc.RootElement.GetProperty("txNo").GetString() != doc.RootElement.GetProperty("toTxNo").GetString());

    // 付款行记支出，收款行记收入
    var fromTx = doc.RootElement.GetProperty("txNo").GetString() ?? "";
    var toTx = doc.RootElement.GetProperty("toTxNo").GetString() ?? "";
    Check("付款行流水带行代码", fromTx.Contains("CCB"), fromTx);
    Check("收款行流水带行代码", toTx.Contains("ICBC"), toTx);
}

// ===== 场景 13：语义触发写操作 =====
Section("场景 13: 语义触发 —— 「我的卡不见了」应触发挂失而不是只返回卡列表");
{
    // 先确认目标卡当前状态正常
    var before = await http.GetStringAsync($"{bankBase}/api/corebank/v1/cards/6222020200000001");
    Check("目标卡初始状态为正常", before.Contains("正常"), "");

    // 演示用户有 6 张卡（3 家银行），语义触发后应追问是哪一张，而不是随便挑一张。
    var ambiguous = await PostChat("\u6211\u7684\u5361\u4E0D\u89C1\u4E86", "u_demo01", Sess("lostcard"));
    Console.WriteLine("  多卡追问: " + S(ambiguous, "content"));
    Check("进入卡片场景", S(ambiguous, "intent").StartsWith("card"), "intent=" + S(ambiguous, "intent"));
    Check("多张卡时追问具体卡号", S(ambiguous, "errorCode") == "CARD_NOT_SPECIFIED", S(ambiguous, "errorCode"));
    Check("追问里列出了候选卡", S(ambiguous, "content").Contains("你名下有"), S(ambiguous, "content"));

    // 指定卡号后应真正执行挂失
    var r13 = await PostChat("\u6211\u7684\u5361\u4E0D\u89C1\u4E86", "u_demo01", Sess("lostcard2"),
        new Dictionary<string, object?> { ["card_no"] = "6222020200000001" });
    Console.WriteLine("  回复: " + S(r13, "content"));
    Check("命中卡片场景", S(r13, "intent").StartsWith("card"), "intent=" + S(r13, "intent"));
    Check("执行了状态变更", S(r13, "resultIntent") == "card.status.updated", S(r13, "resultIntent"));

    var after = await http.GetStringAsync($"{bankBase}/api/corebank/v1/cards/6222020200000001");
    Check("卡片状态已变为挂失", after.Contains("挂失"), "");

    // 复原，避免影响后续运行
    var restore = new StringContent(
        JsonSerializer.Serialize(new { newStatus = "解挂", reason = "e2e restore" }, jsonOpts),
        Encoding.UTF8, "application/json");
    await http.PostAsync($"{bankBase}/api/corebank/v1/cards/6222020200000001/status", restore);
}

// ===== 场景 14：未知意图的友好兜底 =====
Section("场景 14: 未知意图 —— 应给出能力清单而不是死胡同");
{
    var r14 = await PostChat("\u4ECA\u5929\u5929\u6C14\u600E\u4E48\u6837", "u_demo01", Sess("unknown"));
    Console.WriteLine("  回复: " + S(r14, "content"));
    Check("返回未解析错误码", S(r14, "errorCode") == "INTENT_UNRESOLVED", S(r14, "errorCode"));
    Check("回复里列出了可办理的能力", S(r14, "content").Contains("可以办理"), S(r14, "content"));
}

// ===== 场景 15：多轮省略句 =====
Section("场景 15: 多轮上下文 —— 「改成 800」应接上上一轮的转账意图");
{
    var session = Sess("followup");

    // 第一轮：完整表述
    var first = await PostChat("\u7ED9\u674E\u534E\u8F6C500\u5143", "u_demo01", session);
    Console.WriteLine("  第一轮: " + S(first, "content"));
    Check("第一轮识别为转账", S(first, "intent").StartsWith("transfer"), "intent=" + S(first, "intent"));

    // 第二轮：省略句，金额变了，收款人沿用
    var second = await PostChat("\u6539\u6210800\u5143", "u_demo01", session);
    Console.WriteLine("  第二轮: " + S(second, "content"));
    Check("第二轮仍路由到转账", S(second, "intent").StartsWith("transfer"), "intent=" + S(second, "intent"));
    Check("第二轮金额为 800", S(second, "data").Contains("800"), S(second, "data"));
}

// ===== 场景 16：AI 连通性自检 =====
Section("场景 16: AI 连通性自检 —— 让「AI 通没通」可被验证");
{
    var req = new HttpRequestMessage(HttpMethod.Post, $"{hostBase}/api/ai/probe")
    {
        Content = new StringContent("{}", Encoding.UTF8, "application/json")
    };
    req.Headers.Add("Authorization", $"Bearer {token}");
    var resp = await http.SendAsync(req);
    var text = await resp.Content.ReadAsStringAsync();

    Check("自检端点可用", (int)resp.StatusCode == 200, $"HTTP {(int)resp.StatusCode}");
    using var doc = JsonDocument.Parse(text);
    Check("返回模型配置状态", doc.RootElement.TryGetProperty("modelConfigured", out _), "");
    Check("返回一句话结论", doc.RootElement.TryGetProperty("summary", out var sum) && !string.IsNullOrEmpty(sum.GetString()));

    // /health 里应能看到最近一次自检结果
    var healthJson = await http.GetStringAsync($"{hostBase}/health");
    Check("/health 暴露 AI 自检结果", healthJson.Contains("lastProbe"), "");
}

// ===== 场景 17：公募基金 =====
Section("场景 17: 公募基金 —— 代销清单、详情、净值与纯代码路由");
{
    // 核心系统侧：代销清单
    var fundsJson = await http.GetStringAsync($"{bankBase}/api/corebank/v1/funds?limit=100");
    using var fundsDoc = JsonDocument.Parse(fundsJson);
    var fundCount = fundsDoc.RootElement.GetArrayLength();
    Check("代销清单有基金", fundCount >= 8, $"funds={fundCount}");

    // 核心系统侧：单只详情带净值序列
    var detailJson = await http.GetStringAsync($"{bankBase}/api/corebank/v1/funds/000001");
    using var detailDoc = JsonDocument.Parse(detailJson);
    var navPoints = detailDoc.RootElement.GetProperty("navPoints").GetArrayLength();
    Check("基金详情带 90 个净值点", navPoints == 90, $"navPoints={navPoints}");

    // 核心系统侧：排行按近一年收益率倒序
    var rankJson = await http.GetStringAsync($"{bankBase}/api/corebank/v1/funds/rankings?metric=yearly&limit=5");
    using var rankDoc = JsonDocument.Parse(rankJson);
    var returns = rankDoc.RootElement.EnumerateArray()
        .Select(f => f.GetProperty("yearlyReturn").GetDecimal())
        .ToList();
    Check("排行按近一年收益率倒序",
        returns.Zip(returns.Skip(1)).All(p => p.First >= p.Second),
        string.Join(" >= ", returns));

    // 对话入口：基金列表
    var r17 = await PostChat("\u6709\u54EA\u4E9B\u516C\u52DF\u57FA\u91D1", "u_demo01", Sess("funds"));
    Console.WriteLine("  回复: " + S(r17, "content"));
    Check("对话路由到理财场景", S(r17, "intent").StartsWith("wealth"), "intent=" + S(r17, "intent"));
    Check("返回基金结果", S(r17, "resultIntent") == "wealth.funds", S(r17, "resultIntent"));

    // 对话入口：只报一个 6 位代码（没有任何业务关键词）
    var r17b = await PostChat("000001", "u_demo01", Sess("fundcode"));
    Console.WriteLine("  纯代码回复: " + S(r17b, "content"));
    Check("纯 6 位代码也能路由到理财", S(r17b, "intent").StartsWith("wealth"), "intent=" + S(r17b, "intent"));
    Check("纯代码命中具体基金", S(r17b, "content").Contains("000001"), S(r17b, "content"));
}

// ===== 汇总 =====
Section("测试汇总");
Console.WriteLine($"  通过: {passed}    失败: {failed}");
Console.WriteLine(failed == 0 ? "\n  *** 全部通过 ***\n" : $"\n  *** {failed} 项失败 ***\n");
return failed == 0 ? 0 : 1;
