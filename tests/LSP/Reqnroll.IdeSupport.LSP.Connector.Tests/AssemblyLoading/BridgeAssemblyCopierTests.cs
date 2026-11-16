using ReqnrollConnector.AssemblyLoading;
using ReqnrollConnector.Logging;

namespace Reqnroll.IdeSupport.LSP.Connector.Tests.AssemblyLoading;

public class BridgeAssemblyCopierTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bridge-copier-" + Guid.NewGuid().ToString("N"));
    private readonly ILogger _log = Substitute.For<ILogger>();

    public BridgeAssemblyCopierTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public void Copies_the_assembly_when_the_destination_is_missing()
    {
        var src = Write("src.dll", [1, 2, 3]);
        var dst = Path.Combine(_dir, "dst.dll");

        BridgeAssemblyCopier.EnsureCurrent(src, dst, _log);

        File.ReadAllBytes(dst).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Replaces_a_destination_left_by_an_older_build()
    {
        var src = Write("src.dll", [1, 2, 3, 4]);
        var dst = Write("dst.dll", [9, 9, 9, 9]);

        BridgeAssemblyCopier.EnsureCurrent(src, dst, _log);

        File.ReadAllBytes(dst).Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public void Replaces_a_destination_of_a_different_length()
    {
        var src = Write("src.dll", [1, 2, 3]);
        var dst = Write("dst.dll", [1, 2]);

        BridgeAssemblyCopier.EnsureCurrent(src, dst, _log);

        File.ReadAllBytes(dst).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Leaves_an_identical_destination_untouched()
    {
        var src = Write("src.dll", [1, 2, 3]);
        var dst = Write("dst.dll", [1, 2, 3]);
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(dst, stamp);

        BridgeAssemblyCopier.EnsureCurrent(src, dst, _log);

        File.GetLastWriteTimeUtc(dst).Should().Be(stamp);
    }

    [Fact]
    public void Throws_when_a_stale_destination_cannot_be_replaced()
    {
        var src = Write("src.dll", [1, 2, 3]);
        var dst = Write("dst.dll", [9, 9, 9]);
        using var held = new FileStream(dst, FileMode.Open, FileAccess.Read, FileShare.Read);

        var act = () => BridgeAssemblyCopier.EnsureCurrent(src, dst, _log);

        act.Should().Throw<IOException>();
        _log.ReceivedWithAnyArgs().Log(default!);
    }
}
