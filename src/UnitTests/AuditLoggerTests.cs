// ===== 审计链单元测试 =====
// 审计链是合规取证的根基：链断裂意味着证据不可采信。
// 本测试锁定链式签名的正确性，包括并发场景。

using BankingAgent.Base.Security.Audit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace UnitTests;

public class AuditLoggerTests : IDisposable
{
    private readonly string _logPath =
        Path.Combine(Path.GetTempPath(), $"audit-test-{Guid.NewGuid():N}.log");

    private AuditLogger CreateLogger(bool chain = true)
    {
        return new AuditLogger(
            Options.Create(new AuditOptions
            {
                SigningKey = "unit-test-key",
                EnableChainSignature = chain,
                FilePath = _logPath
            }),
            NullLogger<AuditLogger>.Instance);
    }

    private static AuditEvent Evt(string id, string actor = "u_1", decimal? amount = null)
    {
        return new AuditEvent
        {
            AuditId = id,
            Timestamp = DateTimeOffset.UtcNow,
            ActorType = "USER",
            ActorId = actor,
            Operation = "test.op",
            Scenario = "transfer",
            Decision = "SUCCESS",
            Amount = amount
        };
    }

    public void Dispose()
    {
        if (File.Exists(_logPath)) File.Delete(_logPath);
    }

    [Fact]
    public void FirstRecord_HasNoPreviousSignature()
    {
        var logger = CreateLogger();
        var signed = logger.Sign(Evt("a1"));

        Assert.Null(signed.PreviousSignature);
        Assert.Equal(64, signed.Signature.Length);
    }

    [Fact]
    public void SequentialRecords_FormValidChain()
    {
        var logger = CreateLogger();
        var records = new List<AuditEvent>();

        for (var i = 1; i <= 5; i++)
        {
            records.Add(logger.SignAndAdvance(Evt($"a{i}")));
        }

        Assert.Null(records[0].PreviousSignature);
        for (var i = 1; i < records.Count; i++)
        {
            Assert.Equal(records[i - 1].Signature, records[i].PreviousSignature);
        }
    }

    [Fact]
    public void Signatures_AreDeterministic()
    {
        var logger = CreateLogger();
        var e = Evt("fixed-id", "fixed-actor", 100m);

        var s1 = logger.Sign(e);
        var s2 = logger.Sign(e);

        Assert.Equal(s1.Signature, s2.Signature);
    }

    [Fact]
    public void DifferentContent_ProducesDifferentSignature()
    {
        var logger = CreateLogger();
        var s1 = logger.Sign(Evt("id-1", "actor-1", 100m));
        var s2 = logger.Sign(Evt("id-2", "actor-2", 200m));

        Assert.NotEqual(s1.Signature, s2.Signature);
    }

    [Fact]
    public void ChainDisabled_NoSignatureGenerated()
    {
        var logger = CreateLogger(chain: false);
        var signed = logger.Sign(Evt("a1"));

        Assert.Equal("", signed.Signature);
        Assert.Null(signed.PreviousSignature);
    }

    [Fact]
    public void VerifyChain_UntamperedChain_IsValid()
    {
        var logger = CreateLogger();
        var records = new List<AuditEvent>();
        for (var i = 1; i <= 4; i++)
        {
            records.Add(logger.SignAndAdvance(Evt($"a{i}")));
        }

        var verifier = CreateLogger();
        var result = verifier.VerifyChain(records);

        Assert.True(result.IsValid);
        Assert.Equal(4, result.TotalRecords);
        Assert.Empty(result.BrokenIndexes);
    }

    [Fact]
    public void VerifyChain_TamperedPayload_IsDetected()
    {
        var logger = CreateLogger();
        var records = new List<AuditEvent>();
        for (var i = 1; i <= 4; i++)
        {
            records.Add(logger.SignAndAdvance(Evt($"a{i}", amount: 100m)));
        }

        var tampered = records.ToList();
        tampered[2] = tampered[2] with { Amount = 999_999m };

        var verifier = CreateLogger();
        var result = verifier.VerifyChain(tampered);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.BrokenIndexes);
    }

    [Fact]
    public void VerifyChain_BrokenLink_IsDetected()
    {
        var logger = CreateLogger();
        var records = new List<AuditEvent>();
        for (var i = 1; i <= 4; i++)
        {
            records.Add(logger.SignAndAdvance(Evt($"a{i}")));
        }

        // Remove the second record to break the chain
        var broken = records.Where((_, i) => i != 1).ToList();

        var verifier = CreateLogger();
        var result = verifier.VerifyChain(broken);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task WriteAsync_PersistsToFile()
    {
        var logger = CreateLogger();
        await logger.WriteAsync(Evt("w1"));
        await logger.WriteAsync(Evt("w2"));

        var lines = await File.ReadAllLinesAsync(_logPath);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.StartsWith("{", l));
    }

    [Fact]
    public async Task WriteAsync_HashesActorId()
    {
        var logger = CreateLogger();
        await logger.WriteAsync(Evt("w1", actor: "sensitive_user_123"));

        var content = await File.ReadAllTextAsync(_logPath);
        Assert.DoesNotContain("sensitive_user_123", content);
        Assert.Contains("\"actor\":", content);
    }

    [Fact]
    public async Task ConcurrentWrites_ProduceValidChain()
    {
        const int total = 100;
        var logger = CreateLogger();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, total),
            new ParallelOptions { MaxDegreeOfParallelism = 32 },
            async (i, ct) => await logger.WriteAsync(Evt($"c{i:D4}"), ct));

        var lines = await File.ReadAllLinesAsync(_logPath);
        Assert.Equal(total, lines.Length);

        var sigs = new List<string>();
        var prevs = new List<string?>();

        foreach (var line in lines)
        {
            using var d = System.Text.Json.JsonDocument.Parse(line);
            var r = d.RootElement;
            sigs.Add(r.GetProperty("sig").GetString()!);
            prevs.Add(
                r.TryGetProperty("prev", out var p)
                && p.ValueKind == System.Text.Json.JsonValueKind.String
                    ? p.GetString()
                    : null);
        }

        // All signatures must be unique
        Assert.Equal(sigs.Count, sigs.Distinct().Count());

        // Exactly one genesis record
        Assert.Single(prevs, p => p is null);

        // No forks: every prev is referenced at most once
        var forks = prevs
            .Where(p => p is not null)
            .GroupBy(p => p!)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.Empty(forks);
    }
}
