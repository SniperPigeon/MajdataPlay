#nullable enable
using AOT;
using MajdataPlay.Diagnostics;
using ManagedBass;
using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MajdataPlay.IO
{
    /// <summary>
    /// Creates BASS streams whose data is downloaded by a project <see cref="HttpClient"/> instead of BASS's own network code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The response body is fed to a buffered user file stream (<see cref="StreamSystem.Buffer"/>). BASS calls the read
    /// callback on the creating thread while it parses the header, and afterwards on its own download thread, so the
    /// callback may block on the network.
    /// </para>
    /// <para>
    /// BASS calls the close callback when the download reaches the end of the body or when the stream is freed, possibly
    /// while a read is in progress on another thread. Closing disposes the response, which wakes a blocked read.
    /// The callbacks find their source through <see cref="s_sources"/> by an integer id rather than a GCHandle, so a
    /// callback that races with closing finds nothing instead of dereferencing a freed handle.
    /// </para>
    /// <para>
    /// The callbacks are static and marked with <see cref="MonoPInvokeCallbackAttribute"/>, as IL2CPP requires for reverse
    /// P/Invoke. The procedure table is passed as a blittable <see cref="FileProcedurePointers"/>, so no delegate is marshalled
    /// back from native code.
    /// </para>
    /// </remarks>
    internal sealed class BassHttpFileSource
    {
        /// <summary>The sources whose BASS stream may still call back, by the id passed as the callback user data.</summary>
        private static readonly ConcurrentDictionary<int, BassHttpFileSource> s_sources = new ConcurrentDictionary<int, BassHttpFileSource>();
        /// <summary>Roots the close callback for the lifetime of the process.</summary>
        private static readonly FileCloseProcedure s_closeProcedure = OnClose;
        /// <summary>Roots the length callback for the lifetime of the process.</summary>
        private static readonly FileLengthProcedure s_lengthProcedure = OnLength;
        /// <summary>Roots the read callback for the lifetime of the process.</summary>
        private static readonly FileReadProcedure s_readProcedure = OnRead;
        /// <summary>Roots the seek callback for the lifetime of the process.</summary>
        private static readonly FileSeekProcedure s_seekProcedure = OnSeek;
        /// <summary>Aborts a source when the creation token is cancelled.</summary>
        private static readonly Action<object?> s_abortOnCancel = state => ((BassHttpFileSource)state!).Abort();
        /// <summary>The native callback table shared by every stream; BASS copies it during creation.</summary>
        private static readonly FileProcedurePointers s_procedures = new FileProcedurePointers(s_closeProcedure, s_lengthProcedure, s_readProcedure, s_seekProcedure);
        /// <summary>The last id handed out; ids start at one so the user data is never null.</summary>
        private static int s_lastId;

        /// <summary>The response that owns the connection; disposed when the source closes.</summary>
        private readonly HttpResponseMessage _response;
        /// <summary>The response body read by the callbacks.</summary>
        private readonly Stream _content;
        /// <summary>The body length reported to BASS, or zero when it is unknown.</summary>
        private readonly long _length;
        /// <summary>The id passed to BASS as the callback user data.</summary>
        private readonly int _id;
        /// <summary>The URI without its query, for log messages.</summary>
        private readonly string _displayUri;
        /// <summary>Non-zero once the HTTP resources have been released.</summary>
        private int _isAborted;
        /// <summary>Whether stream creation has finished, so reads may return less than requested.</summary>
        private volatile bool _isCreated;
        /// <summary>The first exception thrown by the response body, other than one caused by aborting.</summary>
        private Exception? _readError;

        /// <summary>Gets the number of sources whose BASS stream can still call back. Used by tests to detect leaks.</summary>
        internal static int ActiveSourceCount => s_sources.Count;

        /// <summary>Wraps an HTTP response whose headers have been received.</summary>
        /// <param name="response">The successful response; owned by the new source.</param>
        /// <param name="content">The body stream of <paramref name="response"/>.</param>
        /// <param name="displayUri">The URI without its query, for log messages.</param>
        private BassHttpFileSource(HttpResponseMessage response, Stream content, string displayUri)
        {
            _response = response;
            _content = content;
            _length = GetContentLength(response);
            _displayUri = displayUri;
            _id = Interlocked.Increment(ref s_lastId);
        }

        /// <summary>Downloads <paramref name="uri"/> with <paramref name="client"/> and creates a BASS stream that plays the body.</summary>
        /// <param name="client">The HTTP client that downloads the body.</param>
        /// <param name="uri">The absolute HTTP or HTTPS address of an audio file in a format BASS or a loaded plugin supports.</param>
        /// <param name="flags">The BASS stream flags, for example <see cref="BassFlags.Decode"/>.</param>
        /// <param name="token">Cancels the request and the stream creation. It does not affect the stream once it has been returned.</param>
        /// <returns>The BASS stream handle. Free it with <see cref="Bass.StreamFree(int)"/>, which also closes the download.</returns>
        /// <remarks>
        /// BASS reads the beginning of the body synchronously while it creates the stream, so do not call this on the main thread.
        /// The stream cannot seek beyond the downloaded data.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="client"/> or <paramref name="uri"/> is null.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="token"/> was cancelled or the request was cancelled by the client.</exception>
        /// <exception cref="HttpRequestException">The request failed or the server did not return a success status code.</exception>
        /// <exception cref="IOException">The response body failed before BASS could identify the format.</exception>
        /// <exception cref="BassException">BASS could not create the stream, for example because the format is not supported.</exception>
        public static async ValueTask<int> CreateStreamAsync(HttpClient client, Uri uri, BassFlags flags, CancellationToken token = default)
        {
            if (client is null)
            {
                throw new ArgumentNullException(nameof(client));
            }
            if (uri is null)
            {
                throw new ArgumentNullException(nameof(uri));
            }
            token.ThrowIfCancellationRequested();

            var displayUri = uri.IsAbsoluteUri ? uri.GetLeftPart(UriPartial.Path) : uri.OriginalString;
            var response = default(HttpResponseMessage);
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                {
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                }
                response.EnsureSuccessStatusCode();
                var content = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                var source = new BassHttpFileSource(response, content, displayUri);
                // The source owns the response from here on and releases it on every path.
                response = null;

                return source.CreateStream(flags, token);
            }
            finally
            {
                response?.Dispose();
            }
        }

        /// <summary>Creates the BASS stream on the calling thread.</summary>
        /// <param name="flags">The BASS stream flags.</param>
        /// <param name="token">Aborts the download while BASS reads the header.</param>
        /// <returns>The BASS stream handle.</returns>
        /// <exception cref="OperationCanceledException"><paramref name="token"/> was cancelled.</exception>
        /// <exception cref="IOException">The response body failed before BASS could identify the format.</exception>
        /// <exception cref="BassException">BASS could not create the stream.</exception>
        private int CreateStream(BassFlags flags, CancellationToken token)
        {
            s_sources[_id] = this;
            var stream = 0;
            var error = Errors.OK;
            try
            {
                // Disposing the registration waits for a running abort, so no abort can start after this block.
                using (token.Register(s_abortOnCancel, this))
                {
                    stream = Bass.CreateStream(StreamSystem.Buffer, flags, s_procedures, new IntPtr(_id));
                    error = Bass.LastError;
                }
            }
            catch
            {
                Close();
                throw;
            }

            if (stream == 0)
            {
                // Depending on where creation failed, BASS may already have called the close callback; closing is idempotent.
                Close();
                token.ThrowIfCancellationRequested();
                var readError = Volatile.Read(ref _readError);
                if (readError is not null)
                {
                    throw new IOException($"Downloading {_displayUri} failed while the audio stream was being created.", readError);
                }
                throw new BassException(error);
            }
            if (token.IsCancellationRequested)
            {
                // The abort may have cut the body short after BASS had already accepted the header.
                Bass.StreamFree(stream);
                Close();
                token.ThrowIfCancellationRequested();
            }
            _isCreated = true;
            MajDebug.LogInfo(nameof(BassHttpFileSource), $"Created stream {stream} from {_displayUri}, length {(_length > 0 ? _length.ToString() : "unknown")}");

            return stream;
        }

        /// <summary>Reads body bytes into a BASS buffer.</summary>
        /// <param name="buffer">The native buffer to fill.</param>
        /// <param name="length">The maximum number of bytes to read.</param>
        /// <returns>The number of bytes read; zero at the end of the body or after a failure or abort.</returns>
        private unsafe int Read(IntPtr buffer, int length)
        {
            if (length <= 0)
            {
                return 0;
            }
            var destination = new Span<byte>((void*)buffer, length);
            var total = 0;
            try
            {
                // While BASS parses the header it expects the full request; afterwards any amount is fine.
                do
                {
                    var read = _content.Read(destination.Slice(total));
                    if (read == 0)
                    {
                        break;
                    }
                    total += read;
                }
                while (total < length && !_isCreated);
            }
            catch (Exception e)
            {
                if (Volatile.Read(ref _isAborted) != 0)
                {
                    MajDebug.LogDebug(nameof(BassHttpFileSource), $"Download of {_displayUri} stopped because the stream was closed");
                }
                else
                {
                    Interlocked.CompareExchange(ref _readError, e, null);
                    MajDebug.LogWarning(nameof(BassHttpFileSource), $"Downloading {_displayUri} failed: {e.Message}");
                }
            }

            return total;
        }

        /// <summary>Stops BASS callbacks from reaching this source and releases the HTTP resources. Idempotent.</summary>
        private void Close()
        {
            s_sources.TryRemove(_id, out _);
            Abort();
        }

        /// <summary>Releases the HTTP resources and wakes a blocked read. Idempotent and callable from any thread.</summary>
        private void Abort()
        {
            if (Interlocked.Exchange(ref _isAborted, 1) != 0)
            {
                return;
            }
            try
            {
                _content.Dispose();
                _response.Dispose();
            }
            catch (Exception e)
            {
                MajDebug.LogWarning(nameof(BassHttpFileSource), $"Releasing the response of {_displayUri} failed: {e.Message}");
            }
        }

        /// <summary>Gets the body length to report to BASS.</summary>
        /// <param name="response">The response whose headers describe the body.</param>
        /// <returns>The Content-Length, or zero when it is missing or describes an encoded body that the handler decompresses.</returns>
        private static long GetContentLength(HttpResponseMessage response)
        {
            var headers = response.Content.Headers;
            foreach (var encoding in headers.ContentEncoding)
            {
                if (!string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase))
                {
                    return 0;
                }
            }

            return headers.ContentLength ?? 0;
        }

        /// <summary>Finds the source identified by BASS callback user data.</summary>
        /// <param name="user">The user data passed to <see cref="Bass.CreateStream(StreamSystem, BassFlags, FileProcedurePointers, IntPtr)"/>.</param>
        /// <param name="source">The source, or null when it has been closed.</param>
        /// <returns><see langword="true"/> when the source is still open.</returns>
        private static bool TryGetSource(IntPtr user, [NotNullWhen(true)] out BassHttpFileSource? source)
        {
            return s_sources.TryGetValue((int)user.ToInt64(), out source);
        }

        /// <summary>Handles the BASS close callback.</summary>
        /// <param name="user">The id of the source.</param>
        [MonoPInvokeCallback(typeof(FileCloseProcedure))]
        private static void OnClose(IntPtr user)
        {
            try
            {
                if (TryGetSource(user, out var source))
                {
                    source.Close();
                }
            }
            catch (Exception e)
            {
                MajDebug.LogException(nameof(BassHttpFileSource), e);
            }
        }

        /// <summary>Handles the BASS length callback.</summary>
        /// <param name="user">The id of the source.</param>
        /// <returns>The body length, or zero when it is unknown or the source has been closed.</returns>
        [MonoPInvokeCallback(typeof(FileLengthProcedure))]
        private static long OnLength(IntPtr user)
        {
            return TryGetSource(user, out var source) ? source._length : 0;
        }

        /// <summary>Handles the BASS read callback.</summary>
        /// <param name="buffer">The native buffer to fill.</param>
        /// <param name="length">The maximum number of bytes to read.</param>
        /// <param name="user">The id of the source.</param>
        /// <returns>The number of bytes read; zero marks the end of the file.</returns>
        [MonoPInvokeCallback(typeof(FileReadProcedure))]
        private static int OnRead(IntPtr buffer, int length, IntPtr user)
        {
            try
            {
                return TryGetSource(user, out var source) ? source.Read(buffer, length) : 0;
            }
            catch (Exception e)
            {
                MajDebug.LogException(nameof(BassHttpFileSource), e);
                return 0;
            }
        }

        /// <summary>Handles the BASS seek callback, which buffered file streams never use.</summary>
        /// <param name="offset">The requested position.</param>
        /// <param name="user">The id of the source.</param>
        /// <returns>Always <see langword="false"/>, because an HTTP body cannot seek.</returns>
        [MonoPInvokeCallback(typeof(FileSeekProcedure))]
        private static bool OnSeek(long offset, IntPtr user)
        {
            return false;
        }
    }
}
