using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.LspInterception;

/// <summary>
/// Characterization tests for <see cref="LspFrameCodec"/> — the pure wire-protocol codec extracted
/// from <see cref="LspInterceptingPipe"/> (issue #587, step 1 of the issue's own ordered,
/// low-risk-first list). Exercises the partial/malformed/oversized-frame cases the issue calls out
/// as "currently awkward to reach" through the full pipe, directly against a real
/// <see cref="System.IO.Pipelines.Pipe"/> rather than a live VS↔server connection.
/// </summary>
public class LspFrameCodecTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(2);

    private static byte[] RawFrame(string json)
    {
        var bodyBytes   = Encoding.UTF8.GetBytes(json);
        var headerBytes = Encoding.UTF8.GetBytes($"Content-Length: {bodyBytes.Length}\r\n\r\n");
        var raw = new byte[headerBytes.Length + bodyBytes.Length];
        Array.Copy(headerBytes, 0, raw, 0, headerBytes.Length);
        Array.Copy(bodyBytes, 0, raw, headerBytes.Length, bodyBytes.Length);
        return raw;
    }

    // ── ReadNextFrameAsync: happy path ──────────────────────────────────────

    [Fact]
    public async Task ReadNextFrameAsync_reads_a_complete_frame_and_parses_its_body()
    {
        var pipe = new Pipe();
        var raw = RawFrame("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}");
        await pipe.Writer.WriteAsync(raw);

        var frame = await LspFrameCodec.ReadNextFrameAsync(pipe.Reader, CancellationToken.None);

        frame.Should().NotBeNull();
        frame!.Body.Should().NotBeNull();
        frame.Body!["method"]!.Value<string>().Should().Be("initialize");
        frame.RawBytes.Should().Equal(raw);
    }

    [Fact]
    public async Task ReadNextFrameAsync_delivers_a_frame_split_across_many_small_writes()
    {
        // Feeds the codec's ReadAsync loop bytes one at a time -- exercises both the "haven't seen
        // the full header yet" branch and ReadExactAsync's own multi-iteration body accumulation,
        // neither of which a single WriteAsync(raw) call would ever reach.
        var pipe = new Pipe();
        var raw = RawFrame("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"shutdown\"}");

        var writeTask = Task.Run(async () =>
        {
            foreach (var b in raw)
            {
                await pipe.Writer.WriteAsync(new[] { b });
                await Task.Delay(1);
            }
        });

        var frame = await LspFrameCodec.ReadNextFrameAsync(pipe.Reader, CancellationToken.None);
        await writeTask;

        frame.Should().NotBeNull();
        frame!.Body!["method"]!.Value<string>().Should().Be("shutdown");
        frame.RawBytes.Should().Equal(raw);
    }

    // ── ReadNextFrameAsync: malformed body ──────────────────────────────────

    [Fact]
    public async Task ReadNextFrameAsync_returns_a_null_body_but_preserves_raw_bytes_for_malformed_json()
    {
        var pipe = new Pipe();
        var raw = RawFrame("{not valid json");
        await pipe.Writer.WriteAsync(raw);

        var frame = await LspFrameCodec.ReadNextFrameAsync(pipe.Reader, CancellationToken.None);

        frame.Should().NotBeNull();
        frame!.Body.Should().BeNull();
        frame.RawBytes.Should().Equal(raw, "the caller must still be able to forward the frame verbatim");
    }

    // ── ReadNextFrameAsync: pipe completion at each phase ───────────────────

    [Fact]
    public async Task ReadNextFrameAsync_returns_null_when_the_pipe_completes_before_any_bytes()
    {
        var pipe = new Pipe();
        await pipe.Writer.CompleteAsync();

        var frame = await LspFrameCodec.ReadNextFrameAsync(pipe.Reader, CancellationToken.None);

        frame.Should().BeNull();
    }

    [Fact]
    public async Task ReadNextFrameAsync_returns_null_when_the_pipe_completes_mid_header()
    {
        var pipe = new Pipe();
        // "Content-Length: 5\r\n" with no terminating blank line -- an incomplete header.
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("Content-Length: 5\r\n"));
        await pipe.Writer.CompleteAsync();

        var frame = await LspFrameCodec.ReadNextFrameAsync(pipe.Reader, CancellationToken.None);

        frame.Should().BeNull();
    }

    [Fact]
    public async Task ReadNextFrameAsync_returns_null_when_the_pipe_completes_mid_body()
    {
        // Declares a larger body than is ever delivered ("oversized" relative to what actually
        // arrives) -- the connection drops before the promised bytes show up.
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("Content-Length: 100\r\n\r\n{\"partial"));
        await pipe.Writer.CompleteAsync();

        var frame = await LspFrameCodec.ReadNextFrameAsync(pipe.Reader, CancellationToken.None);

        frame.Should().BeNull();
    }

    [Fact]
    public async Task ReadNextFrameAsync_waits_for_the_rest_of_an_oversized_body_instead_of_returning_early()
    {
        // The declared Content-Length is larger than what has arrived so far but the pipe stays
        // open -- must not return a truncated frame; the second write completes it.
        var pipe = new Pipe();
        var raw = RawFrame("{\"data\":\"" + new string('x', 5000) + "\"}");
        var bodyStart = Array.IndexOf(raw, (byte)'{');
        var header = new byte[bodyStart];
        Array.Copy(raw, header, bodyStart);
        var fullBody = new byte[raw.Length - bodyStart];
        Array.Copy(raw, bodyStart, fullBody, 0, fullBody.Length);

        await pipe.Writer.WriteAsync(header);
        await pipe.Writer.WriteAsync(fullBody.AsMemory(0, 1000));

        var readTask = LspFrameCodec.ReadNextFrameAsync(pipe.Reader, CancellationToken.None);
        await Task.Delay(50); // give ReadNextFrameAsync a chance to (wrongly) return early
        readTask.IsCompleted.Should().BeFalse("only part of the declared Content-Length has arrived");

        await pipe.Writer.WriteAsync(fullBody.AsMemory(1000));
        var frame = await WithTimeoutAsync(readTask, ShortTimeout);

        frame.Should().NotBeNull();
        frame!.RawBytes.Should().Equal(raw);
    }

    // ── EncodeFrame / WriteFrameAsync round-trip ────────────────────────────

    [Fact]
    public async Task EncodeFrame_output_round_trips_through_ReadNextFrameAsync()
    {
        var body = JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":{\"ok\":true}}");
        var encoded = LspFrameCodec.EncodeFrame(body);

        var pipe = new Pipe();
        await LspFrameCodec.WriteFrameAsync(pipe.Writer, encoded, CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        var frame = await LspFrameCodec.ReadNextFrameAsync(pipe.Reader, CancellationToken.None);

        frame.Should().NotBeNull();
        frame!.Body!["id"]!.Value<int>().Should().Be(3);
        frame.Body!["result"]!["ok"]!.Value<bool>().Should().BeTrue();
    }

    // ── TryParseHeader ───────────────────────────────────────────────────────

    [Fact]
    public void TryParseHeader_extracts_content_length_and_header_length()
    {
        var bytes = Encoding.UTF8.GetBytes("Content-Length: 42\r\n\r\n");
        var found = LspFrameCodec.TryParseHeader(new ReadOnlySequence<byte>(bytes), out var contentLength, out var headerLength);

        found.Should().BeTrue();
        contentLength.Should().Be(42);
        headerLength.Should().Be(bytes.Length);
    }

    [Fact]
    public void TryParseHeader_is_case_insensitive_for_the_header_name()
    {
        var bytes = Encoding.UTF8.GetBytes("content-length: 7\r\n\r\n");
        var found = LspFrameCodec.TryParseHeader(new ReadOnlySequence<byte>(bytes), out var contentLength, out _);

        found.Should().BeTrue();
        contentLength.Should().Be(7);
    }

    [Fact]
    public void TryParseHeader_returns_false_when_no_blank_line_terminator_is_present()
    {
        var bytes = Encoding.UTF8.GetBytes("Content-Length: 42\r\n");
        var found = LspFrameCodec.TryParseHeader(new ReadOnlySequence<byte>(bytes), out _, out _);

        found.Should().BeFalse();
    }

    [Fact]
    public void TryParseHeader_returns_false_when_the_Content_Length_header_is_missing()
    {
        var bytes = Encoding.UTF8.GetBytes("Some-Other-Header: value\r\n\r\n");
        var found = LspFrameCodec.TryParseHeader(new ReadOnlySequence<byte>(bytes), out _, out _);

        found.Should().BeFalse();
    }

    [Fact]
    public void TryParseHeader_skips_a_leading_unrelated_header_line_to_find_Content_Length()
    {
        var bytes = Encoding.UTF8.GetBytes("Content-Type: application/vscode-jsonrpc\r\nContent-Length: 15\r\n\r\n");
        var found = LspFrameCodec.TryParseHeader(new ReadOnlySequence<byte>(bytes), out var contentLength, out _);

        found.Should().BeTrue();
        contentLength.Should().Be(15);
    }

#pragma warning disable VSTHRD003
    private static async Task<T> WithTimeoutAsync<T>(Task<T> task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        completed.Should().BeSameAs(task, "the read should complete once the remaining bytes arrive");
        return await task;
    }
#pragma warning restore VSTHRD003
}
