using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Pure LSP wire-protocol codec (<c>Content-Length: N\r\n\r\nBODY</c>) — the first of the seams
/// <c>LspInterceptingPipe</c> is being split into (issue #587, step 1 of the issue's own ordered,
/// low-risk-first list). Carries no session state: everything here operates only on the
/// <see cref="PipeReader"/>/<see cref="PipeWriter"/> or raw bytes passed in, which is what makes it
/// independently testable against captured frames — including the partial/malformed/oversized cases
/// that are awkward to reach through the full pipe.
/// </summary>
/// <remarks>
/// Deliberately NOT touching correlation/routing, the pump loops, or session termination in this
/// change — per the issue's explicit sequencing, this class needs its own design pass before any
/// more of it moves, and should not be attempted opportunistically alongside other work.
/// </remarks>
internal static class LspFrameCodec
{
    /// <summary>Shared UTF-8 encoding without a byte-order-mark preamble, matching the LSP wire format.</summary>
    internal static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Initial capacity used while accumulating a body, so a large declared length cannot force a
    /// large allocation up front. This — not any cap on the declared length — is what keeps a bogus
    /// <c>Content-Length</c> from turning into an <see cref="OutOfMemoryException"/>: nothing is
    /// allocated on the strength of the number, only on bytes that actually arrive.
    /// </summary>
    private const int BodyAccumulatorInitialCapacity = 64 * 1024;

    /// <summary>Longest malformed header echoed into a log message.</summary>
    private const int MalformedHeaderLogLimit = 200;

    /// <summary>Outcome of inspecting a buffer for a complete, usable LSP header block.</summary>
    public enum HeaderParseResult
    {
        /// <summary>No <c>\r\n\r\n</c> terminator yet — more bytes may complete the header.</summary>
        Incomplete,

        /// <summary>A usable <c>Content-Length</c> was found.</summary>
        Parsed,

        /// <summary>
        /// The header block is complete but carries no usable <c>Content-Length</c> — missing,
        /// non-numeric, negative, or larger than a body can be represented as. More bytes cannot
        /// fix it.
        /// </summary>
        Malformed
    }

    /// <summary>One decoded LSP frame: the parsed JSON body (when parseable) plus its original raw bytes for verbatim forwarding.</summary>
    internal sealed class LspFrame
    {
        public LspFrame(JObject? body, byte[] rawBytes) { Body = body; RawBytes = rawBytes; }

        private LspFrame(string malformedHeaderText)
        {
            Body                = null;
            RawBytes            = Array.Empty<byte>();
            MalformedHeaderText = malformedHeaderText;
        }

        public JObject? Body     { get; }
        public byte[]   RawBytes { get; }

        /// <summary>
        /// True when the frame's header block was complete but unusable (see
        /// <see cref="HeaderParseResult.Malformed"/>). Distinct from a malformed <em>body</em>: there
        /// the body's extent is known, so <see cref="RawBytes"/> can still be forwarded verbatim;
        /// here it is not, so there is nothing to forward and the caller must log and carry on.
        /// </summary>
        public bool HasMalformedHeader => MalformedHeaderText is not null;

        /// <summary>The offending header block (truncated), for logging; <c>null</c> unless <see cref="HasMalformedHeader"/>.</summary>
        public string? MalformedHeaderText { get; }

        /// <summary>Creates the malformed-header sentinel for <paramref name="headerText"/>.</summary>
        public static LspFrame ForMalformedHeader(string headerText) => new LspFrame(headerText);
    }

    /// <summary>
    /// Reads one LSP frame from <paramref name="reader"/>.
    /// Returns <c>null</c> when the pipe is completed (remote side closed) before a full frame
    /// arrives — whether that happens before any bytes, mid-header, or mid-body.
    /// Returns an <see cref="LspFrame"/> with a <c>null</c> <see cref="LspFrame.Body"/> when
    /// JSON parsing fails; raw bytes are still present so the caller can forward verbatim.
    /// Returns <see cref="LspFrame.HasMalformedHeader"/> when the header block is complete but
    /// unusable — the header bytes are consumed so the reader can resynchronise on whatever
    /// follows, and the caller is expected to log and keep pumping.
    /// </summary>
    public static async Task<LspFrame?> ReadNextFrameAsync(PipeReader reader, CancellationToken ct)
    {
        // Phase 1 – read until we see \r\n\r\n and can extract Content-Length.
        // We use AdvanceTo(consumed, examined) correctly: we only mark bytes as consumed
        // once we know exactly which bytes belong to the header vs. the body.
        long contentLength;   // parsed at the server peer's width; TryParseHeader guarantees it fits an int when Parsed.
        int headerLength; // total byte length of "Content-Length: N\r\n\r\n"

        while (true)
        {
            var result = await reader.ReadAsync(ct).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (result.IsCompleted && buffer.IsEmpty)
                return null;

            var headerParse = TryParseHeader(buffer, out contentLength, out headerLength);

            if (headerParse == HeaderParseResult.Parsed)
            {
                // Mark exactly the header bytes as consumed; leave body bytes in the pipe.
                reader.AdvanceTo(buffer.GetPosition(headerLength));
                break;
            }

            if (headerParse == HeaderParseResult.Malformed)
            {
                // Waiting for more bytes cannot fix an already-complete header, and the body's
                // extent is unknowable without a length — so consume just the header block and let
                // the caller decide. Anything that follows is re-examined as a fresh header, which
                // resynchronises on the next well-formed frame.
                var malformedHeaderText = DescribeHeader(buffer, headerLength);
                reader.AdvanceTo(buffer.GetPosition(headerLength));
                return LspFrame.ForMalformedHeader(malformedHeaderText);
            }

            // Haven't seen the full header yet – tell the pipe we've examined everything
            // but consumed nothing so it can give us more data next time.
            reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
                return null; // pipe ended mid-header
        }

        // Phase 2 – read exactly contentLength body bytes.
        var bodyBytes = await ReadExactAsync(reader, (int)contentLength, ct).ConfigureAwait(false);
        if (bodyBytes is null)
            return null;

        // Re-build raw frame for verbatim forwarding.
        var headerText = $"Content-Length: {contentLength}\r\n\r\n";
        var headerEnc  = Utf8NoBom.GetBytes(headerText);
        var rawBytes   = new byte[headerEnc.Length + bodyBytes.Length];
        Array.Copy(headerEnc, 0, rawBytes, 0, headerEnc.Length);
        Array.Copy(bodyBytes, 0, rawBytes, headerEnc.Length, bodyBytes.Length);

        JObject? body;
        try
        {
            body = JObject.Parse(Utf8NoBom.GetString(bodyBytes));
        }
        catch (Exception)
        {
            body = null; // malformed JSON — caller forwards raw bytes without intercepting
        }

        return new LspFrame(body, rawBytes);
    }

    /// <summary>Re-encodes a (possibly mutated) parsed body back into a raw LSP frame.</summary>
    public static byte[] EncodeFrame(JObject body)
    {
        // Deliberately the parameterless overload: JToken.ToString(Formatting) resolves to a
        // MissingMethodException in the VS host process — some Newtonsoft.Json assembly loaded
        // there doesn't carry that overload. The parameterless one is used successfully
        // elsewhere in this codebase (e.g. GoToHooksService). Formatting (indented vs. compact)
        // doesn't affect wire correctness, only payload size.
        var bodyBytes   = Utf8NoBom.GetBytes(body.ToString());
        var headerText  = $"Content-Length: {bodyBytes.Length}\r\n\r\n";
        var headerBytes = Utf8NoBom.GetBytes(headerText);

        var rawBytes = new byte[headerBytes.Length + bodyBytes.Length];
        Array.Copy(headerBytes, 0, rawBytes, 0, headerBytes.Length);
        Array.Copy(bodyBytes, 0, rawBytes, headerBytes.Length, bodyBytes.Length);
        return rawBytes;
    }

    /// <summary>
    /// Tries to find the LSP header block (terminated by <c>\r\n\r\n</c>) in
    /// <paramref name="buffer"/> and extract the <c>Content-Length</c> value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first <c>\r\n\r\n</c> ends the header block by definition, so a block that carries no
    /// usable <c>Content-Length</c> is <see cref="HeaderParseResult.Malformed"/> rather than
    /// something a later terminator might rescue.
    /// </para>
    /// <para>
    /// "Usable" means: parses as a <see cref="long"/> (matching
    /// <c>OmniSharp.Extensions.JsonRpc</c>, the reader on the server end of this pipe, which also
    /// parses the value as a <see cref="long"/>), is not negative, and is small enough for a body to
    /// be represented at all. That last bound is the platform's, not a policy: the body is handed
    /// back as a <see cref="byte"/> array, which cannot be longer than <see cref="int.MaxValue"/>.
    /// <b>No size limit of our own is imposed</b> — neither peer of this pipe imposes one either
    /// (<c>StreamJsonRpc</c>, VS's client, and <c>OmniSharp.Extensions.JsonRpc</c> both bound only
    /// the header value's textual length), and the LSP base protocol specifies none.
    /// </para>
    /// <para>
    /// Each rejected case was a live failure. A negative value reached
    /// <see cref="ReadExactAsync"/>'s allocation and threw <see cref="ArgumentOutOfRangeException"/>
    /// out of the codec and — via the receive pump's catch-all — stopped relaying server output for
    /// the rest of the process's life, from a single corrupt frame. A non-numeric value looked
    /// <see cref="HeaderParseResult.Incomplete"/>, so the reader waited for bytes that could never
    /// fix an already-complete header. An unrepresentable one would stall the same way, since the
    /// body it promises can never be delivered.
    /// </para>
    /// </remarks>
    public static HeaderParseResult TryParseHeader(ReadOnlySequence<byte> buffer, out long contentLength, out int headerLength)
    {
        contentLength = 0;
        headerLength  = 0;

        // Flatten to a single array only if the buffer is multi-segment (rare for small headers).
        var bytes = buffer.IsSingleSegment
            ? buffer.First.Span.ToArray()
            : buffer.ToArray();

        for (int i = 0; i <= bytes.Length - 4; i++)
        {
            if (bytes[i] == '\r' && bytes[i + 1] == '\n' &&
                bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
            {
                headerLength = i + 4; // header bytes + \r\n\r\n — reported for both outcomes below,
                                      // so a malformed block can be skipped rather than re-read.

                var headerText = Utf8NoBom.GetString(bytes, 0, i);
                foreach (var line in headerText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        var valueStr = line.Substring("Content-Length:".Length).Trim();
                        if (long.TryParse(valueStr, out contentLength) &&
                            contentLength >= 0 && contentLength <= int.MaxValue)
                        {
                            return HeaderParseResult.Parsed;
                        }

                        contentLength = 0;
                        return HeaderParseResult.Malformed;
                    }
                }

                return HeaderParseResult.Malformed; // complete block, no Content-Length at all
            }
        }

        headerLength = 0;
        return HeaderParseResult.Incomplete;
    }

    /// <summary>Decodes the first <paramref name="headerLength"/> bytes of <paramref name="buffer"/> for a log message, truncated.</summary>
    private static string DescribeHeader(ReadOnlySequence<byte> buffer, int headerLength)
    {
        var take  = Math.Min(headerLength, MalformedHeaderLogLimit);
        var bytes = buffer.Slice(0, take).ToArray();
        return Utf8NoBom.GetString(bytes).Replace("\r", "\\r").Replace("\n", "\\n");
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes from <paramref name="reader"/>, or <c>null</c> if the pipe completes first.</summary>
    /// <remarks>
    /// <paramref name="count"/> is expected to have been validated by <see cref="TryParseHeader"/>.
    /// The accumulator is deliberately not pre-sized to <paramref name="count"/>: a legitimate but
    /// large declared length would otherwise force the whole allocation before a single body byte
    /// has arrived.
    /// </remarks>
    public static async Task<byte[]?> ReadExactAsync(PipeReader reader, int count, CancellationToken ct)
    {
        var accumulator = new List<byte>(Math.Min(count, BodyAccumulatorInitialCapacity));

        while (accumulator.Count < count)
        {
            var result = await reader.ReadAsync(ct).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (result.IsCompleted && buffer.IsEmpty)
                return null;

            int needed = count - accumulator.Count;
            var slice  = buffer.Length >= needed ? buffer.Slice(0, needed) : buffer;

            foreach (var seg in slice)
            {
                accumulator.AddRange(seg.ToArray());
            }

            reader.AdvanceTo(slice.End);
        }

        return accumulator.ToArray();
    }

    /// <summary>Writes a raw, already-encoded LSP frame to <paramref name="writer"/> and flushes.</summary>
    public static async Task WriteFrameAsync(PipeWriter writer, byte[] rawFrame, CancellationToken ct)
    {
        var memory = writer.GetMemory(rawFrame.Length);
        rawFrame.CopyTo(memory);
        writer.Advance(rawFrame.Length);
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }
}
