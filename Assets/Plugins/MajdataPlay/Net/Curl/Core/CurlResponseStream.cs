using MajdataPlay.Buffers;
using MajdataPlay.Net.Curl.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
#nullable enable
namespace MajdataPlay.Net.Curl.Core
{
    /// <summary>
    /// Read-only stream that hands response body bytes from the libcurl worker thread (the writer) to the caller (the reader).
    /// </summary>
    /// <remarks>
    /// Every field shared by the writer and the reader, including the pooled buffers, is guarded by <see cref="_sync"/>.
    /// <see cref="Dispose(bool)"/> and <see cref="Abort(Exception)"/> may therefore run on any thread, even while another
    /// thread is blocked in <see cref="Read(byte[], int, int)"/>, without returning a buffer to the pool while it is still
    /// being copied. Disposing releases the buffers immediately; only the native easy handle is released by the owner
    /// through the dispose callback.
    /// </remarks>
    internal class CurlResponseStream : Stream
    {
        /// <inheritdoc/>
        public override bool CanRead => true;

        /// <inheritdoc/>
        public override bool CanSeek => false;

        /// <inheritdoc/>
        public override bool CanWrite => false;

        /// <inheritdoc/>
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc/>
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>
        /// Guards every mutable field below and is the monitor a blocked reader waits on.
        /// </summary>
        readonly object _sync = new();

        /// <summary>
        /// Chunks written by libcurl that the reader has not started yet.
        /// </summary>
        readonly Queue<MemoryChunk> _readyChunks = new();

        /// <summary>
        /// Buffered byte count at or below which a paused transfer is resumed.
        /// </summary>
        readonly int _lwmBytes;

        /// <summary>
        /// Buffered byte count above which the transfer is paused.
        /// </summary>
        readonly int _hwmBytes;

        /// <summary>
        /// Asks the owner to resume a paused transfer. Invoked outside <see cref="_sync"/>.
        /// </summary>
        readonly Action _onResume;

        /// <summary>
        /// Tells the owner that the reader is gone, so the native transfer can be released. Invoked once, outside <see cref="_sync"/>.
        /// </summary>
        readonly Action _onDispose;

        /// <summary>
        /// The chunk the reader is currently copying from.
        /// </summary>
        MemoryChunk _currentChunk;

        /// <summary>
        /// The reason the transfer was aborted, rethrown by every later read.
        /// </summary>
        Exception? _abortException;

        /// <summary>
        /// Bytes written by libcurl and not read yet.
        /// </summary>
        int _bufferedBytes;

        /// <summary>
        /// Whether the last write asked libcurl to pause the transfer.
        /// </summary>
        bool _isPaused;

        /// <summary>
        /// Whether no more chunks will be written, because the transfer completed, failed or the stream was disposed.
        /// </summary>
        bool _isWritingCompleted;

        /// <summary>
        /// Whether the reader disposed the stream.
        /// </summary>
        bool _isDisposed;

        /// <summary>
        /// Creates an empty response body stream.
        /// </summary>
        /// <param name="maxBufferLength">The number of buffered bytes the pause and resume thresholds are derived from.</param>
        /// <param name="onResume">Asks the owner to resume a paused transfer.</param>
        /// <param name="onDispose">Tells the owner that the stream has been disposed.</param>
        public CurlResponseStream(long maxBufferLength, Action onResume, Action onDispose)
        {
            _onResume = onResume;
            _onDispose = onDispose;
            _lwmBytes = (int)(maxBufferLength * 0.4);
            _hwmBytes = (int)(maxBufferLength * 0.8);
        }

        /// <summary>
        /// Appends a block of body bytes. Called by the libcurl write callback on the worker thread.
        /// </summary>
        /// <param name="chunk">The bytes delivered by libcurl.</param>
        /// <returns>
        /// The number of bytes consumed, <see cref="CurlCallbackReturn.Write.Pause"/> when the reader is too far behind,
        /// or <see cref="CurlCallbackReturn.Write.Error"/> when the stream no longer accepts data.
        /// </returns>
        public UIntPtr WriteChunk(ReadOnlySpan<byte> chunk)
        {
            if (chunk.IsEmpty)
            {
                return UIntPtr.Zero;
            }
            lock (_sync)
            {
                if (_isDisposed || _isWritingCompleted)
                {
                    return CurlCallbackReturn.Write.Error;
                }
                // Only pause while the reader still has bytes to drain; otherwise no read would ever resume the transfer.
                if (_bufferedBytes > 0 && (_bufferedBytes + chunk.Length) > _hwmBytes)
                {
                    _isPaused = true;
                    return CurlCallbackReturn.Write.Pause;
                }
                try
                {
                    var buffer = Pool<byte>.Rent(chunk.Length);
                    chunk.CopyTo(buffer.AsSpan());
                    _readyChunks.Enqueue(new MemoryChunk(buffer, chunk.Length));
                }
                catch
                {
                    // An exception must never unwind through libcurl's native frames.
                    return CurlCallbackReturn.Write.Error;
                }
                _bufferedBytes += chunk.Length;
                Monitor.PulseAll(_sync);
            }

            return (UIntPtr)chunk.Length;
        }

        /// <summary>
        /// Marks the body as complete, so reads return 0 once the buffered bytes have been consumed.
        /// </summary>
        public void CompleteWriting()
        {
            lock (_sync)
            {
                _isWritingCompleted = true;
                Monitor.PulseAll(_sync);
            }
        }

        /// <summary>
        /// Aborts the body. Every later read throws <paramref name="ex"/>, so the buffered bytes are released at once.
        /// The first abort reason is kept.
        /// </summary>
        /// <param name="ex">The exception later reads throw.</param>
        public void Abort(Exception ex)
        {
            lock (_sync)
            {
                if (_isDisposed)
                {
                    return;
                }
                _abortException ??= ex;
                _isWritingCompleted = true;
                ReleaseBufferedChunks();
                Monitor.PulseAll(_sync);
            }
        }

        /// <summary>
        /// Reads body bytes, blocking until at least one byte is available, the body ends, or the stream is aborted or disposed.
        /// </summary>
        /// <param name="buffer">The destination array.</param>
        /// <param name="offset">The offset in <paramref name="buffer"/> at which to start writing.</param>
        /// <param name="count">The maximum number of bytes to read.</param>
        /// <returns>The number of bytes read; 0 at the end of the body.</returns>
        /// <exception cref="ObjectDisposedException">The stream was disposed before or during the read.</exception>
        /// <exception cref="Exception">The abort reason passed to <see cref="Abort(Exception)"/>.</exception>
        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesRead = 0;
            var shouldResume = false;
            lock (_sync)
            {
                while (true)
                {
                    ThrowIfDisposed();
                    if (_abortException is not null)
                    {
                        ExceptionDispatchInfo.Capture(_abortException).Throw();
                    }
                    if (count == 0 || _currentChunk.Remaining > 0)
                    {
                        break;
                    }
                    ReleaseChunk(ref _currentChunk);
                    if (_readyChunks.Count > 0)
                    {
                        _currentChunk = _readyChunks.Dequeue();
                        continue;
                    }
                    if (_isWritingCompleted)
                    {
                        return 0;
                    }
                    Monitor.Wait(_sync);
                }
                bytesRead = Math.Min(count, _currentChunk.Remaining);
                _currentChunk.Buffer.AsSpan(_currentChunk.Offset, bytesRead).CopyTo(buffer.AsSpan(offset, bytesRead));
                _currentChunk.Offset += bytesRead;
                _bufferedBytes -= bytesRead;
                if (_isPaused && _bufferedBytes <= _lwmBytes)
                {
                    _isPaused = false;
                    shouldResume = true;
                }
            }
            if (shouldResume)
            {
                _onResume();
            }

            return bytesRead;
        }

        /// <inheritdoc/>
        public override void Flush()
        {
        }

        /// <inheritdoc/>
        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc/>
        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc/>
        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Releases the buffered bytes, wakes a blocked reader and, on the first call, notifies the owner.
        /// </summary>
        /// <param name="disposing">Whether the call comes from <see cref="Stream.Dispose()"/> rather than a finalizer.</param>
        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing && MarkDisposed())
                {
                    _onDispose();
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }

        /// <summary>
        /// Marks the stream disposed and releases its buffers.
        /// </summary>
        /// <returns><see langword="true"/> for the first call; <see langword="false"/> when the stream was already disposed.</returns>
        bool MarkDisposed()
        {
            lock (_sync)
            {
                if (_isDisposed)
                {
                    return false;
                }
                _isDisposed = true;
                _isWritingCompleted = true;
                ReleaseBufferedChunks();
                Monitor.PulseAll(_sync);
                return true;
            }
        }

        /// <summary>
        /// Returns every buffered chunk to the pool. Must be called while holding <see cref="_sync"/>.
        /// </summary>
        void ReleaseBufferedChunks()
        {
            ReleaseChunk(ref _currentChunk);
            while (_readyChunks.Count > 0)
            {
                var chunk = _readyChunks.Dequeue();
                ReleaseChunk(ref chunk);
            }
            _bufferedBytes = 0;
        }

        /// <summary>
        /// Returns <paramref name="chunk"/>'s buffer to the pool and clears it.
        /// </summary>
        /// <param name="chunk">The chunk to release; reset to an empty chunk.</param>
        static void ReleaseChunk(ref MemoryChunk chunk)
        {
            chunk.Buffer.Dispose();
            chunk = default;
        }

        /// <summary>
        /// Throws if the stream has been disposed. Must be called while holding <see cref="_sync"/>.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The stream has been disposed.</exception>
        void ThrowIfDisposed()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(CurlResponseStream));
            }
        }

        /// <summary>
        /// A pooled buffer holding one libcurl write, and how much of it the reader has consumed.
        /// </summary>
        struct MemoryChunk
        {
            /// <summary>
            /// The pooled buffer; a field so that disposing it updates this chunk rather than a copy.
            /// </summary>
            public PooledArray<byte> Buffer;

            /// <summary>
            /// The number of valid bytes in <see cref="Buffer"/>.
            /// </summary>
            public int Length;

            /// <summary>
            /// The number of bytes already read.
            /// </summary>
            public int Offset;

            /// <summary>
            /// Gets the number of bytes left to read.
            /// </summary>
            public readonly int Remaining => Length - Offset;

            /// <summary>
            /// Creates a chunk holding <paramref name="length"/> bytes of <paramref name="buffer"/>.
            /// </summary>
            /// <param name="buffer">The pooled buffer.</param>
            /// <param name="length">The number of valid bytes.</param>
            public MemoryChunk(PooledArray<byte> buffer, int length)
            {
                Buffer = buffer;
                Length = length;
                Offset = 0;
            }
        }
    }
}
