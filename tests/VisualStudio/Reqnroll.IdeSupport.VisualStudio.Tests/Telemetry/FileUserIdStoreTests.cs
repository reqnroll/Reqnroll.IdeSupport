using Reqnroll.IdeSupport.VisualStudio.Telemetry;
namespace Reqnroll.IdeSupport.VisualStudio.Tests.Telemetry;

public class FileUserIdStoreTests
{
    private const string UserId = "491ed5c0-9f25-4c27-941a-19b17cc81c87";
    private IFileSystemForVs fileSystemStub;

    [Fact]
    public void Should_GetUserIdFromFile_WhenFileExists()
    {
        var sut = CreateSut();

        GivenFileExists();
        GivenUserIdStringInFile(UserId);

        string userId = sut.GetUserId();

        userId.Should().Be(UserId);
    }

    [Fact]
    public void Should_NormalisePaddedGuidText_WhenReadingFromFile()
    {
        var sut = CreateSut();

        GivenFileExists();
        GivenUserIdStringInFile($"  {UserId}  ");

        string userId = sut.GetUserId();

        userId.Should().Be(UserId);
    }

    [Fact]
    public void Should_PersistNewlyGeneratedUserId_WhenNoUserIdExists()
    {
        var sut = CreateSut();

        GivenFileDoesNotExists();

        string userId = sut.GetUserId();

        userId.Should().NotBeEmpty();
        fileSystemStub.File.Received(1).WriteAllText(UserIdFilePath(), userId);
    }

    [Fact]
    public void Should_ReturnInMemoryUserId_WhenPersistingTheNewUserIdFails()
    {
        var sut = CreateSut();

        GivenFileDoesNotExists();
        GivenPersistFails();

        string userId = sut.GetUserId();

        Guid.TryParse(userId, out _).Should().BeTrue();
    }

    [Fact]
    public void Should_ReturnInMemoryUserId_WhenReadingTheExistingUserIdFails()
    {
        var sut = CreateSut();

        GivenFileExists();
        fileSystemStub.File.ReadAllText(Arg.Any<string>())
            .Returns(_ => throw new UnauthorizedAccessException("profile is locked"));

        string userId = sut.GetUserId();

        Guid.TryParse(userId, out _).Should().BeTrue();
    }

    [Fact]
    public void Should_NotThrowFromTheLazy_WhenTheProfileIsUnwritable()
    {
        var sut = CreateSut();

        GivenFileDoesNotExists();
        GivenPersistFails();

        // GetUserId runs from the transmitter's MEF constructor; a faulted Lazy would cache the
        // exception and rethrow on every subsequent access, breaking the telemetry chain for the
        // whole session.
        var act = () => sut.GetUserId();

        act.Should().NotThrow();
    }

    [Fact]
    public void Should_ReturnTheSameFallbackUserId_OnRepeatedCalls_WhenProfileIsUnwritable()
    {
        var sut = CreateSut();

        GivenFileDoesNotExists();
        GivenPersistFails();

        var first = sut.GetUserId();
        var second = sut.GetUserId();

        second.Should().Be(first);
    }

    [Fact]
    public void Should_ReturnNullPath_WhenAppDataIsNotSet()
    {
        FileUserIdStore.ResolveUserIdFilePath(null).Should().BeNull();
    }

    [Fact]
    public void Should_ReturnNullPath_WhenAppDataIsRelative()
    {
        FileUserIdStore.ResolveUserIdFilePath(@"some\relative\path").Should().BeNull();
    }

    [Fact]
    public void Should_CombinePath_WhenAppDataIsAbsolute()
    {
        FileUserIdStore.ResolveUserIdFilePath(@"C:\Users\someone\AppData\Roaming")
            .Should().Be(@"C:\Users\someone\AppData\Roaming\Reqnroll\userid");
    }

    public FileUserIdStore CreateSut()
    {
        fileSystemStub = Substitute.For<IFileSystemForVs>();
        return new FileUserIdStore(fileSystemStub);
    }

    private static string UserIdFilePath() => FileUserIdStore.UserIdFilePath!;

    private void GivenFileExists()
    {
        fileSystemStub.File.Exists(Arg.Any<string>()).Returns(true);
    }

    private void GivenFileDoesNotExists()
    {
        fileSystemStub.File.Exists(Arg.Any<string>()).Returns(false);
        fileSystemStub.Directory.Exists(Arg.Any<string>()).Returns(true);
    }

    private void GivenPersistFails()
    {
        fileSystemStub.File
            .When(f => f.WriteAllText(Arg.Any<string>(), Arg.Any<string>()))
            .Do(_ => throw new UnauthorizedAccessException("profile is read-only"));
    }

    private void GivenUserIdStringInFile(string userIdString)
    {
        fileSystemStub.File.ReadAllText(Arg.Any<string>()).Returns(userIdString);
    }
}
