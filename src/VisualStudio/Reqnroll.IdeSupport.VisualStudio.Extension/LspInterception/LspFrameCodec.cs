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

    /// <summary>One decoded LSP frame: the parsed JSON body (when parseable) plus its original raw bytes for verbatim forwarding.</summary>
    internal sealed class LspFrame
    {
        public LspFrame(JObject? body, byte[] rawBytes) { Body = body; RawBytes = rawBytes; }
        public JObject? Body     { get; }
        public byte[]   RawBytes { get; }
    }

    /// <summary>
    /// Reads one LSP frame from <paramref name="reader"/>.
    /// Returns <c>null</c> when the pipe is completed (remote side closed) before a full frame
    /// arrives — whether that happens before any bytes, mid-header, or mid-body.
    /// Returns an <see cref="LspFrame"/> with a <c>null</c> <see cref="LspFrame.Body"/> when
    /// JSON parsing fails; raw bytes are still present so the caller can forward verbatim.
    /// </summary>
    public static async Task<LspFrame?> ReadNextFrameAsync(PipeReader reader, CancellationToken ct)
    {
        // Phase 1 – read until we see \r\n\r\n and can extract Content-Length.
        // We use AdvanceTo(consumed, examined) correctly: we only mark bytes as consumed
        // once we know exactly which bytes belong to the header vs. the body.
        int contentLength;
        int headerLength; // total byte length of "Content-Length: N\r\n\r\n"

        while (true)
        {
            var result = await reader.ReadAsync(ct).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (result.IsCompleted && buffer.IsEmpty)
                return null;

            if (TryParseHeader(buffer, out contentLength, out headerLength))
            {
                // Mark exactly the header bytes as consumed; leave body bytes in the pipe.
                reader.AdvanceTo(buffer.GetPosition(headerLength));
                break;
            }

            // Haven't seen the full header yet – tell the pipe we've examined everything
            // but consumed nothing so it can give us more data next time.
            reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
                return null; // pipe ended mid-header
        }

        // Phase 2 – read exactly contentLength body bytes.
        var bodyBytes = await ReadExactAsync(reader, contentLength, ct).ConfigureAwait(false);
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
    public static bool TryParseHeader(ReadOnlySequence<byte> buffer, out int contentLength, out int headerLength)
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
                var headerText = Utf8NoBom.GetString(bytes, 0, i);
                foreach (var line in headerText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        var valueStr = line.Substring("Content-Length:".Length).Trim();
                        if (int.TryParse(valueStr, out contentLength))
                        {
                            headerLength = i + 4; // header bytes + \r\n\r\n
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes from <paramref name="reader"/>, or <c>null</c> if the pipe completes first.</summary>
    public static async Task<byte[]?> ReadExactAsync(PipeReader reader, int count, CancellationToken ct)
    {
        var accumulator = new List<byte>(count);

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
