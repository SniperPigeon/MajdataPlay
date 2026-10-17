using System;
using MajdataPlay.IO;

internal static partial class Program
{
    /// <summary>Checks channel reduction, 16-bit scaling, wraparound, overflow accounting, and whole-frame reads of the audio recorder buffer.</summary>
    static void VerifyAudioSampleRingBuffer()
    {
        var buffer = new AudioSampleRingBuffer(2, 4);
        // A 4-channel mix keeps only its front pair.
        buffer.Write(new float[] { 1, 2, 9, 9, 3, 4, 9, 9 }, 4);
        Check(buffer.AvailableFrames == 2, "multichannel writes keep one frame per source frame");
        var output = new float[8];
        Check(buffer.Read(output.AsSpan(0, 3)) == 2, "reads return only whole frames");
        Check(output[0] == 1 && output[1] == 2, "the front pair is kept in channel order");

        buffer.Write(new short[] { 16384, -32768, 0, 32767, 8192, 8192 }, 2);
        Check(buffer.AvailableFrames == 4, "16-bit frames fill the remaining capacity across the wrap point");
        buffer.Write(new float[] { 7, 7, 8, 8 }, 2);
        Check(buffer.AvailableFrames == 4 && buffer.DroppedFrames == 2, "a full buffer drops new frames instead of overwriting");

        Array.Clear(output, 0, output.Length);
        Check(buffer.Read(output) == 8, "a read drains every buffered frame");
        Check(output[0] == 3 && output[1] == 4, "older frames are read first");
        Check(output[2] == 0.5f && output[3] == -1f && output[4] == 0f && output[5] == 32767f / 32768f,
            "16-bit samples are scaled to the nominal float range");
        Check(output[6] == 0.25f && output[7] == 0.25f, "frames written after the wrap point are read in order");
        Check(buffer.Read(output) == 0, "an empty buffer reads nothing");

        buffer.Clear();
        Check(buffer.DroppedFrames == 0 && buffer.AvailableFrames == 0, "clearing resets the frame and dropped counts");
        buffer.Write(new float[] { 5, 6, 7 }, 2);
        Check(buffer.AvailableFrames == 1, "a trailing partial frame is ignored");
        Expect<ArgumentOutOfRangeException>(() => buffer.Write(new float[] { 1 }, 1), "a source with fewer channels is rejected");
        Expect<ArgumentOutOfRangeException>(() => new AudioSampleRingBuffer(0, 4), "zero channels are rejected");
        Expect<ArgumentOutOfRangeException>(() => new AudioSampleRingBuffer(2, 0), "zero capacity is rejected");
    }

    /// <summary>Requires an operation to throw the expected exception type.</summary>
    /// <typeparam name="TException">The expected exception type.</typeparam>
    /// <param name="operation">The operation to run.</param>
    /// <param name="message">The expectation description.</param>
    static void Expect<TException>(Action operation, string message) where TException : Exception
    {
        try
        {
            operation();
        }
        catch (TException)
        {
            Check(true, message);
            return;
        }
        throw new InvalidOperationException(message);
    }
}
