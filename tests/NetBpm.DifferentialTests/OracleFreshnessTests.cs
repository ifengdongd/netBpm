using System.Diagnostics;
using NetBpm.DifferentialTests.Differential;

namespace NetBpm.DifferentialTests;

/// <summary>
/// 真值新鲜度：语料输入（BPMN / <c>case.json</c>）被改过之后，预言机快照必须重跑。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要它：真值（<c>&lt;stem&gt;.oracle.json</c>）是**人工**跑 Java 预言机产出的
/// （JDK 21 + 本地 install 的 Activiti 9.0.0，见 <c>tools/Oracle/README.md</c>），
/// CI 只消费它、不生产它。语料改了而真值没重跑，对拍比的就是「新语料 vs 旧真值」——
/// 差异看着像引擎退步，其实是真值过期，而且越晚发现越难定位。
/// </para>
/// <para>
/// 判定用 <c>git log</c> 的<b>提交时间</b>而不是文件 mtime：git 不保留 mtime，
/// clone / checkout 之后所有文件的 mtime 都是 checkout 时刻，拿它比会在本地恒红、在 CI 恒绿 ——
/// 两头都没意义。提交时间是仓库里唯一跨环境稳定的"最后改动时刻"。
/// </para>
/// <para>
/// ⚠️ 需要完整历史：浅克隆（<c>--depth 1</c>）里所有文件的提交时间都相同，检查会静默通过。
/// CI 的 <c>differential</c> job 因此显式 <c>fetch-depth: 0</c>。
/// </para>
/// </remarks>
public sealed class OracleFreshnessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OracleSnapshots_ShouldBeNewerThanCorpusInputs()
    {
        var root = TestDatabase.RepositoryRoot;

        if (!await IsInsideWorkTreeAsync(root).ConfigureAwait(false))
        {
            Assert.Skip("不在 git 工作树中（或没有 git），无法判断真值新鲜度。");
        }

        var cases = CorpusRepository.Load(TestDatabase.CorpusRoot);

        // 先取「最新真值」的提交时间：绝大多数语料都不该比它更晚，
        // 只有命中这一关的才需要再精确查自己那份真值。
        var latestOracle = await LastCommitUnixSecondsAsync(root, "tests/corpus/**/*.oracle.json")
            .ConfigureAwait(false);

        var stale = new List<string>();

        foreach (var entry in cases)
        {
            if (!File.Exists(entry.OracleFile))
            {
                // 没有真值的语料只产出被测侧快照，不参与比对，也就谈不上过期
                continue;
            }

            var inputs = await LastCommitUnixSecondsAsync(
                root,
                Relative(root, entry.BpmnFile),
                Relative(root, entry.CaseFile)).ConfigureAwait(false);

            if (inputs is null || inputs <= latestOracle)
            {
                continue;
            }

            var oracle = await LastCommitUnixSecondsAsync(root, Relative(root, entry.OracleFile))
                .ConfigureAwait(false);

            if (oracle is null || inputs > oracle)
            {
                stale.Add($"{entry.FullName}（语料 {Unix(inputs)} → 真值 {Unix(oracle)}）");
            }
        }

        Assert.True(
            stale.Count == 0,
            $"{stale.Count} 条语料在真值生成之后被改过，此时对拍比的是「新语料 vs 旧真值」，"
            + $"差异不能作为引擎结论：{string.Join("；", stale.Take(10))}"
            + "。重跑方式见 tools/Oracle/README.md（mvn -f tools/Oracle/pom.xml compile exec:java）。");
    }

    private static string Relative(string root, string file) =>
        Path.GetRelativePath(root, file).Replace('\\', '/');

    private static string Unix(long? seconds) =>
        seconds is null
            ? "(无提交)"
            : DateTimeOffset.FromUnixTimeSeconds(seconds.Value).ToString("yyyy-MM-dd", Culture);

    private static readonly System.Globalization.CultureInfo Culture =
        System.Globalization.CultureInfo.InvariantCulture;

    private static async ValueTask<bool> IsInsideWorkTreeAsync(string root)
    {
        try
        {
            var output = await RunGitAsync(root, "rev-parse", "--is-inside-work-tree").ConfigureAwait(false);

            return string.Equals(output.Trim(), "true", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>取触及任一给定路径的最后一次提交的 Unix 秒；没有提交返回 <see langword="null"/>。</summary>
    private static async ValueTask<long?> LastCommitUnixSecondsAsync(string root, params string[] paths)
    {
        var arguments = new List<string> { "log", "-1", "--format=%ct", "--" };

        foreach (var path in paths)
        {
            arguments.Add(path.Contains('*', StringComparison.Ordinal) ? $":(glob){path}" : path);
        }

        var output = await RunGitAsync(root, arguments.ToArray()).ConfigureAwait(false);
        var text = output.Trim();

        return text.Length == 0 ? null : long.Parse(text, Culture);
    }

    private static async ValueTask<string> RunGitAsync(string root, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("无法启动 git。");

        var stdout = await process.StandardOutput.ReadToEndAsync(Ct).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(Ct).ConfigureAwait(false);

        await process.WaitForExitAsync(Ct).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} 失败：{stderr.Trim()}");
        }

        return stdout;
    }
}
