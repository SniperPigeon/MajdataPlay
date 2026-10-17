using ManagedBass;
using System;
using System.Collections.Generic;
using System.Text;

namespace MajdataPlay.Diagnostics
{
    internal static class ErrorsExtensions
    {
        public static void LogDebug(this Errors errCode)
        {
            MajDebug.LogDebug("Audio", $"[Bass]Error: {errCode}");
        }
        public static void LogInfo(this Errors errCode)
        {
            MajDebug.LogInfo("Audio", $"[Bass]Error: {errCode}");
        }
        public static void LogWarning(this Errors errCode)
        {
            MajDebug.LogWarning("Audio", $"[Bass]Error: {errCode}");
        }
        public static void LogError(this Errors errCode)
        {
            MajDebug.LogError("Audio", $"[Bass]Error: {errCode}");
        }
    }
}
