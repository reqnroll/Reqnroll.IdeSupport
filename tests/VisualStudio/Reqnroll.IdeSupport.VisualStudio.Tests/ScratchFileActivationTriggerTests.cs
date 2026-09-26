using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.Extension;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests;

/// <summary>
/// Covers the parts of the scratch-file activation trigger (issue #533) that run without VS: the
/// activation signal, the decision rules and the scratch file itself.
/// </summary>
public class ScratchFileActivationTriggerTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "ReqnrollTests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    // ── LanguageServerActivationSignal ─────────────────────────────────────

    // A unique key per signal keeps tests independent of each other and of the shared instance.
    private static LanguageServerActivationSignal NewSignal(string? key = null) =>
        new(key ?? "Reqnroll.IdeSupport.Tests.Activated." + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Signals_with_the_same_key_share_state()
    {
        // Stands in for two copies of the extension assembly loaded in one process.
        var key = "Reqnroll.IdeSupport.Tests.Activated." + Guid.NewGuid().ToString("N");
        var providerSide = NewSignal(key);
        var packageSide = NewSignal(key);

        providerSide.MarkActivated();

        packageSide.IsActivated.Should().BeTrue();
    }

    [Fact]
    public void Signal_starts_not_activated()
    {
        NewSignal().IsActivated.Should().BeFalse();
    }

    [Fact]
    public async Task Wait_returns_true_at_once_when_already_activated()
    {
        var signal = NewSignal();
        signal.MarkActivated();

        (await signal.WaitAsync(TimeSpan.FromMinutes(1), CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Wait_returns_true_when_activated_while_waiting()
    {
        var signal = NewSignal();

        var wait = signal.WaitAsync(TimeSpan.FromMinutes(1), CancellationToken.None);
        signal.MarkActivated();

        (await wait).Should().BeTrue();
    }

    [Fact]
    public async Task Wait_returns_false_when_the_timeout_expires()
    {
        var signal = NewSignal();

        (await signal.WaitAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Wait_throws_when_cancelled()
    {
        var signal = NewSignal();
        using var cts = new CancellationTokenSource();

        cts.CancelAfter(TimeSpan.FromMilliseconds(20));

        await ((Func<Task>)(() => signal.WaitAsync(TimeSpan.FromMinutes(1), cts.Token)))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void MarkActivated_is_idempotent()
    {
        var signal = NewSignal();

        signal.MarkActivated();
        signal.MarkActivated();

        signal.IsActivated.Should().BeTrue();
    }

    // ── ActivationTriggerRules ─────────────────────────────────────────────

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("yes", true)]
    public void Disable_variable_turns_the_trigger_off_for_any_value_but_empty_0_or_false(string? value, bool disabled)
    {
        ActivationTriggerRules.IsDisabledValue(value).Should().Be(disabled);
    }

    [Fact]
    public void Triggers_when_a_feature_document_is_open_and_the_provider_never_activated()
    {
        ActivationTriggerRules.Decide(disabled: false, providerActivated: false, openFeatureDocuments: 1)
            .Should().Be(ActivationTriggerDecision.Trigger);
    }

    [Fact]
    public void Does_not_trigger_when_the_provider_already_activated()
    {
        ActivationTriggerRules.Decide(disabled: false, providerActivated: true, openFeatureDocuments: 3)
            .Should().Be(ActivationTriggerDecision.SkipAlreadyActivated);
    }

    [Fact]
    public void Does_not_trigger_when_no_feature_document_is_open()
    {
        ActivationTriggerRules.Decide(disabled: false, providerActivated: false, openFeatureDocuments: 0)
            .Should().Be(ActivationTriggerDecision.SkipNoFeatureDocument);
    }

    [Fact]
    public void The_kill_switch_wins_over_everything_else()
    {
        ActivationTriggerRules.Decide(disabled: true, providerActivated: false, openFeatureDocuments: 1)
            .Should().Be(ActivationTriggerDecision.SkipDisabled);
    }

    // ── Scratch file ───────────────────────────────────────────────────────

    [Fact]
    public void Scratch_file_is_created_as_a_feature_file_under_a_Reqnroll_temp_folder()
    {
        var path = ScratchFileActivationTrigger.EnsureScratchFile(_tempRoot);

        path.Should().Be(Path.Combine(_tempRoot, "Reqnroll", "ReqnrollActivation.feature"));
        File.ReadAllText(path).Should().Be(ScratchFileActivationTrigger.ScratchFileContent);
    }

    [Fact]
    public void Scratch_file_content_is_a_feature_with_no_scenarios()
    {
        ScratchFileActivationTrigger.ScratchFileContent.Should().Contain("\nFeature: ");
        ScratchFileActivationTrigger.ScratchFileContent.Should().NotContain("Scenario");
    }

    [Fact]
    public void Scratch_file_with_unexpected_content_is_rewritten()
    {
        var path = ScratchFileActivationTrigger.EnsureScratchFile(_tempRoot);
        File.WriteAllText(path, "Feature: edited by someone\r\nScenario: not ours\r\n");

        ScratchFileActivationTrigger.EnsureScratchFile(_tempRoot);

        File.ReadAllText(path).Should().Be(ScratchFileActivationTrigger.ScratchFileContent);
    }

    [Fact]
    public void Existing_scratch_file_with_expected_content_is_reused_unchanged()
    {
        var path = ScratchFileActivationTrigger.EnsureScratchFile(_tempRoot);
        var written = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, written);

        ScratchFileActivationTrigger.EnsureScratchFile(_tempRoot);

        File.GetLastWriteTimeUtc(path).Should().Be(written);
    }
}
