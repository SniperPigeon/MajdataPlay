#nullable enable
using AOT;
using MajdataPlay.Diagnostics;
using ManagedBass;
using System;
using System.Runtime.InteropServices;

namespace MajdataPlay.IO
{
    /// <summary>Captures the final mix of a Bass output channel through a DSP callback.</summary>
    /// <remarks>
    /// Each instance attaches its own DSP and owns its own buffer, so several recorders on one channel run independently.
    /// The DSP runs on the Bass output thread after every other DSP on the channel and copies the mix into a
    /// bounded buffer under a short lock. At most the front stereo pair of a multichannel device mix is kept,
    /// because <see cref="AudioManager.MixingMatrix"/> routes the stereo game mix to every speaker pair.
    /// </remarks>
    internal sealed class BassAudioRecorder : IAudioRecorder
    {
        /// <summary>Limits recorded channels to the front stereo pair.</summary>
        private const int MaximumRecordedChannels = 2;
        /// <summary>Places this DSP after all other DSP and FX on the channel so it observes the final mix.</summary>
        private const int DspPriority = int.MinValue;
        /// <summary>Roots the native DSP callback for IL2CPP and Mono.</summary>
        private static readonly DSPProcedure s_dspProcedure = OnDsp;
        /// <summary>Synchronizes the Bass output thread with Start, Stop, Read, and Dispose.</summary>
        private readonly object _gate = new object();
        /// <summary>Stores captured frames until the consumer reads them.</summary>
        private readonly AudioSampleRingBuffer _buffer;
        /// <summary>Stores the Bass channel whose output is captured.</summary>
        private readonly int _channel;
        /// <summary>Stores the interleaved channel count of the Bass channel.</summary>
        private readonly int _sourceChannels;
        /// <summary>Indicates whether the DSP receives 32-bit float samples rather than signed 16-bit samples.</summary>
        private readonly bool _isFloatSource;
        /// <summary>Identifies this instance to the static DSP callback.</summary>
        private GCHandle _selfHandle;
        /// <summary>Stores the DSP handle, or zero after removal.</summary>
        private int _dsp;
        /// <summary>Indicates whether the DSP copies samples into the buffer.</summary>
        private bool _isRecording;
        /// <summary>Indicates whether the DSP has been removed and the recorder released.</summary>
        private bool _isDisposed;
        /// <summary>Stores the first failure inside the DSP callback, which cannot throw across the native boundary.</summary>
        private Exception? _captureError;

        /// <summary>Attaches a capture DSP to a Bass output channel.</summary>
        /// <param name="channel">The Bass mixer or device output stream whose final mix is captured.</param>
        /// <param name="bufferSeconds">The number of seconds of audio buffered for a slow consumer, at least one.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="bufferSeconds"/> is less than one.</exception>
        /// <exception cref="NotSupportedException">The channel uses 8-bit samples.</exception>
        /// <exception cref="InvalidOperationException">Bass cannot describe the channel or attach the DSP.</exception>
        public BassAudioRecorder(int channel, int bufferSeconds)
        {
            if (bufferSeconds < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferSeconds));
            }
            if (!Bass.ChannelGetInfo(channel, out var info))
            {
                throw new InvalidOperationException($"Cannot read the Bass output channel format: {Bass.LastError}");
            }
            // FloatingPointDSP makes Bass pass float samples to every DSP regardless of the channel format.
            _isFloatSource = info.Resolution == Resolution.Float || Bass.FloatingPointDSP;
            if (!_isFloatSource && info.Resolution != Resolution.Short)
            {
                throw new NotSupportedException("Recording an 8-bit Bass output channel is not supported.");
            }

            _channel = channel;
            _sourceChannels = info.Channels;
            SampleRate = info.Frequency;
            Channels = Math.Min(info.Channels, MaximumRecordedChannels);
            _buffer = new AudioSampleRingBuffer(Channels, checked(SampleRate * bufferSeconds));
            _selfHandle = GCHandle.Alloc(this);
            _dsp = Bass.ChannelSetDSP(channel, s_dspProcedure, GCHandle.ToIntPtr(_selfHandle), DspPriority);
            if (_dsp == 0)
            {
                var error = Bass.LastError;
                _selfHandle.Free();
                throw new InvalidOperationException($"Cannot attach the recording DSP to the Bass output channel: {error}");
            }
            MajDebug.LogInfo(nameof(BassAudioRecorder), $"Attached to channel {channel}: {SampleRate} Hz, "
                + $"{_sourceChannels} source channels, recording {Channels}, float={_isFloatSource}");
        }

        /// <inheritdoc/>
        public int SampleRate { get; }
        /// <inheritdoc/>
        public int Channels { get; }
        /// <inheritdoc/>
        public bool IsRecording
        {
            get
            {
                lock (_gate)
                {
                    return _isRecording;
                }
            }
        }
        /// <inheritdoc/>
        public long DroppedFrames
        {
            get
            {
                lock (_gate)
                {
                    return _buffer.DroppedFrames;
                }
            }
        }

        /// <inheritdoc/>
        public void Start()
        {
            lock (_gate)
            {
                EnsureIsOpen();
                if (_isRecording)
                {
                    return;
                }
                _buffer.Clear();
                _captureError = null;
                _isRecording = true;
            }
        }

        /// <inheritdoc/>
        public void Stop()
        {
            Exception? captureError;
            long droppedFrames;
            lock (_gate)
            {
                EnsureIsOpen();
                if (!_isRecording)
                {
                    return;
                }
                _isRecording = false;
                captureError = _captureError;
                droppedFrames = _buffer.DroppedFrames;
            }
            if (captureError is not null)
            {
                MajDebug.LogError(nameof(BassAudioRecorder), $"Audio capture failed during recording: {captureError}");
            }
            if (droppedFrames > 0)
            {
                MajDebug.LogWarning(nameof(BassAudioRecorder), $"Dropped {droppedFrames} frames because the consumer fell behind");
            }
        }

        /// <inheritdoc/>
        public int Read(Span<float> destination)
        {
            lock (_gate)
            {
                EnsureIsOpen();
                return _buffer.Read(destination);
            }
        }

        /// <summary>Removes the capture DSP and discards buffered audio.</summary>
        /// <remarks>Repeated disposal has no effect. The Bass output channel itself is not freed.</remarks>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_isDisposed)
                {
                    return;
                }
                _isDisposed = true;
                _isRecording = false;
            }
            // Bass serializes DSP removal with the channel's DSP chain, so no callback runs after this returns.
            if (_dsp != 0 && !Bass.ChannelRemoveDSP(_channel, _dsp))
            {
                MajDebug.LogWarning(nameof(BassAudioRecorder), $"Cannot remove the recording DSP: {Bass.LastError}");
            }
            _dsp = 0;
            if (_selfHandle.IsAllocated)
            {
                _selfHandle.Free();
            }
        }

        /// <summary>Copies one block of the final mix into the buffer while recording.</summary>
        /// <param name="buffer">The borrowed native sample block.</param>
        /// <param name="length">The block length in bytes.</param>
        private unsafe void Capture(IntPtr buffer, int length)
        {
            lock (_gate)
            {
                if (!_isRecording)
                {
                    return;
                }
                if (_isFloatSource)
                {
                    _buffer.Write(new ReadOnlySpan<float>((void*)buffer, length / sizeof(float)), _sourceChannels);
                }
                else
                {
                    _buffer.Write(new ReadOnlySpan<short>((void*)buffer, length / sizeof(short)), _sourceChannels);
                }
            }
        }

        /// <summary>Records a callback failure for the next Stop without throwing on the Bass thread.</summary>
        /// <param name="error">The failure raised while capturing.</param>
        private void ReportCaptureError(Exception error)
        {
            lock (_gate)
            {
                _captureError ??= error;
            }
        }

        /// <summary>Throws when the recorder has been disposed; call while holding the gate.</summary>
        /// <exception cref="ObjectDisposedException">The recorder has been disposed.</exception>
        private void EnsureIsOpen()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(BassAudioRecorder));
            }
        }

        /// <summary>Forwards a Bass DSP block to the recorder identified by the user pointer.</summary>
        /// <param name="handle">The DSP handle.</param>
        /// <param name="channel">The channel the DSP is attached to.</param>
        /// <param name="buffer">The borrowed sample block.</param>
        /// <param name="length">The block length in bytes.</param>
        /// <param name="user">The GCHandle pointer of the recorder.</param>
        [MonoPInvokeCallback(typeof(DSPProcedure))]
        private static void OnDsp(int handle, int channel, IntPtr buffer, int length, IntPtr user)
        {
            BassAudioRecorder? recorder = null;
            try
            {
                recorder = GCHandle.FromIntPtr(user).Target as BassAudioRecorder;
                recorder?.Capture(buffer, length);
            }
            catch (Exception error)
            {
                recorder?.ReportCaptureError(error);
            }
        }
    }
}
