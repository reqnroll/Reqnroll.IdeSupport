using Microsoft.Testing.Platform.Extensions.Messages;
using Microsoft.Testing.Platform.Extensions.TestFramework;

namespace Reqnroll.IdeSupport.TestReporter.MTP.Tests.Mtp;

/// <summary>
/// A Microsoft.Testing.Platform test framework that publishes a fixed list of <see cref="TestNode"/>
/// updates — the in-process harness for issue #741 T0, after the pattern Tyrrrz/GitHubActionsTestLogger
/// uses (<c>GitHubActionsTestLogger.Tests/Mtp/FakeTestFramework.cs</c>). It drives the reporter through
/// MTP's real pipeline (extension enablement, session lifetime, message bus) with no <c>dotnet test</c>
/// process and no fixture project.
/// </summary>
internal sealed class FakeTestFramework(IReadOnlyList<TestNode> testNodes) : Microsoft.Testing.Platform.Extensions.TestFramework.ITestFramework, IDataProducer
{
    public string Uid => nameof(FakeTestFramework);
    public string Version => "1.0.0";
    public string DisplayName => Uid;
    public string Description => "Publishes a fixed list of test node updates.";
    public Type[] DataTypesProduced { get; } = [typeof(TestNodeUpdateMessage)];

    public Task<bool> IsEnabledAsync() => Task.FromResult(true);

    public Task<CreateTestSessionResult> CreateTestSessionAsync(CreateTestSessionContext context) =>
        Task.FromResult(new CreateTestSessionResult { IsSuccess = true });

    public async Task ExecuteRequestAsync(ExecuteRequestContext context)
    {
        try
        {
            foreach (var testNode in testNodes)
                await context.MessageBus.PublishAsync(this, new TestNodeUpdateMessage(context.Request.Session.SessionUid, testNode));
        }
        finally
        {
            context.Complete();
        }
    }

    public Task<CloseTestSessionResult> CloseTestSessionAsync(CloseTestSessionContext context) =>
        Task.FromResult(new CloseTestSessionResult { IsSuccess = true });
}
