using Reqnroll.IdeSupport.VisualStudio.Telemetry;
namespace Reqnroll.IdeSupport.VisualStudio.Tests.Telemetry;

public class FileInstallationIdStoreTests
{
    private const string InstallationId = "491ed5c0-9f25-4c27-941a-19b17cc81c87";
    private IFileSystemForVs fileSystemStub;

    [Fact]
    public void Should_GetInstallationIdFromFile_WhenFileExists()
    {
        var sut = CreateSut();

        GivenFileExists();
        GivenInstallationIdStringInFile(InstallationId);

        string installationId = sut.GetInstallationId();

        installationId.Should().Be(InstallationId);
    }

    [Fact]
    public void Should_NormalisePaddedGuidText_WhenReadingFromFile()
    {
        var sut = CreateSut();

        GivenFileExists();
        GivenInstallationIdStringInFile($"  {InstallationId}  ");

        string installationId = sut.GetInstallationId();

        installationId.Should().Be(InstallationId);
    }

    [Fact]
    public void Should_PersistNewlyGeneratedInstallationId_WhenNoInstallationIdExists()
    {
        var sut = CreateSut();

        GivenFileDoesNotExists();

        string installationId = sut.GetInstallationId();

        installationId.Should().NotBeEmpty();
        fileSystemStub.File.Received(1).WriteAllText(InstallationIdFilePath(), installationId);
    }

    [Fact]
    public void Should_ReturnInMemoryInstallationId_WhenPersistingTheNewInstallationIdFails()
    {
        var sut = CreateSut();

        GivenFileDoesNotExists();
        GivenPersistFails();

        string installationId = sut.GetInstallationId();

        Guid.TryParse(installationId, out _).Should().BeTrue();
    }

    [Fact]
    public void Should_ReturnInMemoryInstallationId_WhenReadingTheExistingInstallationIdFails()
    {
        var sut = CreateSut();

        GivenFileExists();
        fileSystemStub.File.ReadAllText(Arg.Any<string>())
            .Returns(_ => throw new UnauthorizedAccessException("profile is locked"));

        string installationId = sut.GetInstallationId();

        Guid.TryParse(installationId, out _).Should().BeTrue();
    }

    [Fact]
    public void Should_NotThrowFromTheLazy_WhenTheProfileIsUnwritable()
    {
        var sut = CreateSut();

        GivenFileDoesNotExists();
        GivenPersistFails();

        // GetInstallationId runs when the first permitted event is sent; a faulted Lazy would cache the
        // exception and rethrow on every subsequent access, breaking the telemetry chain for the
        // whole session.
        var act = () => sut.GetInstallationId();

        act.Should().NotThrow();
    }

    [Fact]
    public void Should_ReturnTheSameFallbackInstallationId_OnRepeatedCalls_WhenProfileIsUnwritable()
    {
        var sut = CreateSut();

        GivenFileDoesNotExists();
        GivenPersistFails();

        var first = sut.GetInstallationId();
        var second = sut.GetInstallationId();

        second.Should().Be(first);
    }

    [Fact]
    public void Should_ReturnNullPath_WhenLocalAppDataIsNotSet()
    {
        FileInstallationIdStore.ResolveInstallationIdFilePath(null).Should().BeNull();
    }

    [Fact]
    public void Should_ReturnNullPath_WhenLocalAppDataIsRelative()
    {
        FileInstallationIdStore.ResolveInstallationIdFilePath(@"some\relative\path").Should().BeNull();
    }

    [Fact]
    public void Should_CombinePath_WhenLocalAppDataIsAbsolute()
    {
        FileInstallationIdStore.ResolveInstallationIdFilePath(@"C:\Users\someone\AppData\Local")
            .Should().Be(@"C:\Users\someone\AppData\Local\Reqnroll\installationid");
    }

    [Fact]
    public void Should_StoreTheIdInTheLocalProfile_NotTheRoamingOne()
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrEmpty(localAppData)) return; // nothing to compare against on this agent

        FileInstallationIdStore.InstallationIdFilePath.Should()
            .Be(System.IO.Path.Combine(localAppData, "Reqnroll", "installationid"));
    }

    public FileInstallationIdStore CreateSut()
    {
        fileSystemStub = Substitute.For<IFileSystemForVs>();
        return new FileInstallationIdStore(fileSystemStub);
    }

    private static string InstallationIdFilePath() => FileInstallationIdStore.InstallationIdFilePath!;

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

    private void GivenInstallationIdStringInFile(string installationIdString)
    {
        fileSystemStub.File.ReadAllText(Arg.Any<string>()).Returns(installationIdString);
    }
}
