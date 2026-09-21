using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.TestOutcomes;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.TestOutcomes;

/// <summary>
/// Phase 1 of the MTP test-outcome plan (issue #715): the breadcrumb a future MTP reporter discovers
/// the LSP server's listener endpoint through, since MTP has no per-run config injection channel the
/// way VSTest's runsettings does. Covers write/delete shape and stale-entry pruning directly, without
/// spawning real processes — <see cref="TestOutcomeTcpListenerBreadcrumbTests"/> (LSP.Server.Tests)
/// covers the real bind/dispose cycle end to end.
/// </summary>
public class TestOutcomeSessionBreadcrumbTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "reqnroll-outcome-breadcrumb-tests", Guid.NewGuid().ToString("N"));
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private TestOutcomeSessionBreadcrumb Breadcrumb(int pid = 111, Func<int, bool>? isProcessRunning = null)
        => new(_dir, pid, isProcessRunning ?? (_ => true), _logger);

    [Fact]
    public void Write_creates_a_file_named_by_pid_with_the_expected_shape()
    {
        var breadcrumb = Breadcrumb(pid: 4242);

        breadcrumb.Write("127.0.0.1:53412", @"C:\repo\MySolution");

        var path = Path.Combine(_dir, "4242.json");
        File.Exists(path).Should().BeTrue();
        var json = JObject.Parse(File.ReadAllText(path));
        json.Value<string>("endpoint").Should().Be("127.0.0.1:53412");
        json.Value<string>("workspaceRoot").Should().Be(@"C:\repo\MySolution");
        json.Value<int>("lspServerPid").Should().Be(4242);
        json.Value<DateTime?>("startedUtc").Should().NotBeNull();
    }

    [Fact]
    public void Write_tolerates_a_null_workspace_root()
    {
        var breadcrumb = Breadcrumb(pid: 5);

        breadcrumb.Write("127.0.0.1:1", workspaceRoot: null);

        var json = JObject.Parse(File.ReadAllText(Path.Combine(_dir, "5.json")));
        json["workspaceRoot"]!.Type.Should().Be(Newtonsoft.Json.Linq.JTokenType.Null);
    }

    [Fact]
    public void Write_called_again_overwrites_rather_than_leaving_two_files()
    {
        var breadcrumb = Breadcrumb(pid: 7);

        breadcrumb.Write("127.0.0.1:1", "root-a");
        breadcrumb.Write("127.0.0.1:2", "root-b");

        Directory.GetFiles(_dir, "*.json").Should().ContainSingle();
        JObject.Parse(File.ReadAllText(Path.Combine(_dir, "7.json"))).Value<string>("endpoint").Should().Be("127.0.0.1:2");
    }

    [Fact]
    public void Delete_removes_the_written_file()
    {
        var breadcrumb = Breadcrumb(pid: 9);
        breadcrumb.Write("127.0.0.1:1", null);

        breadcrumb.Delete();

        File.Exists(Path.Combine(_dir, "9.json")).Should().BeFalse();
    }

    [Fact]
    public void Delete_without_a_prior_write_is_a_no_op()
    {
        var breadcrumb = Breadcrumb(pid: 11);

        Action act = () => breadcrumb.Delete();

        act.Should().NotThrow();
    }

    [Fact]
    public void PruneStaleEntries_when_the_directory_does_not_exist_does_nothing()
    {
        var breadcrumb = Breadcrumb();

        Action act = () => breadcrumb.PruneStaleEntries();

        act.Should().NotThrow();
    }

    [Fact]
    public void PruneStaleEntries_deletes_only_dead_pid_files_and_ignores_non_numeric_names()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "100.json"), "{}"); // stale
        File.WriteAllText(Path.Combine(_dir, "200.json"), "{}"); // stale
        File.WriteAllText(Path.Combine(_dir, "300.json"), "{}"); // live
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "irrelevant");

        var breadcrumb = Breadcrumb(pid: 999, isProcessRunning: pid => pid == 300);

        breadcrumb.PruneStaleEntries();

        Directory.GetFiles(_dir).Select(Path.GetFileName).Should().BeEquivalentTo("300.json", "notes.txt");
    }

    [Fact]
    public void Write_prunes_stale_entries_before_writing_its_own()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "100.json"), "{}"); // stale
        File.WriteAllText(Path.Combine(_dir, "200.json"), "{}"); // live

        var breadcrumb = Breadcrumb(pid: 999, isProcessRunning: pid => pid == 200);

        breadcrumb.Write("127.0.0.1:1", null);

        Directory.GetFiles(_dir, "*.json").Select(Path.GetFileName)
            .Should().BeEquivalentTo("200.json", "999.json");
    }
}
