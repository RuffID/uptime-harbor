using Xunit;

namespace UptimeHarbor.Tests.Web;

public class HttpClientContractTests
{
    [Fact]
    public void HttpHelper_DefinesSafeErrorAndCancellationBranches()
    {
        string source = ReadSource("shared", "http.ts");

        Assert.Contains("export class ApiError", source, StringComparison.Ordinal);
        Assert.Contains("public readonly status", source, StringComparison.Ordinal);
        Assert.Contains("if (!text.trim())", source, StringComparison.Ordinal);
        Assert.Contains("в неподдерживаемом формате", source, StringComparison.Ordinal);
        Assert.Contains("export function isAbortError", source, StringComparison.Ordinal);
        Assert.Contains("error.name === \"AbortError\"", source, StringComparison.Ordinal);
        Assert.Contains("Сервер вернул пустой JSON-ответ", source, StringComparison.Ordinal);
        Assert.DoesNotContain("parsed?.message ?? text", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardClient_CoalescesRefreshAndCancelsStaleSnapshot()
    {
        string source = ReadSource("pages", "dashboard.ts");

        Assert.Contains("SNAPSHOT_REFRESH_DEBOUNCE_MS = 200", source, StringComparison.Ordinal);
        Assert.Contains("scheduleSnapshotRefresh()", source, StringComparison.Ordinal);
        Assert.Contains("snapshotAbortController?.abort()", source, StringComparison.Ordinal);
        Assert.Contains("abortController.signal", source, StringComparison.Ordinal);
        Assert.Contains("if (isAbortError(error))", source, StringComparison.Ordinal);
        Assert.Contains("hubConnection.onreconnected", source, StringComparison.Ordinal);
        Assert.Contains("void loadSnapshot(false)", source, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] segments)
    {
        string sourcePath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "UptimeHarbor.Web",
                "wwwroot",
                "ts",
                Path.Combine(segments)));
        return File.ReadAllText(sourcePath);
    }
}
