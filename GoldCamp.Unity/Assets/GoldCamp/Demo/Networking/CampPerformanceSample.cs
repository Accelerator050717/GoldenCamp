using System;
using System.IO;
using UnityEngine;

namespace GoldCamp.Demo
{
    // Development diagnostics in marked test roots. Frame intervals are observed
    // player update time, not GPU timing or a minimum hardware guarantee.
    public sealed class CampPerformanceSample : MonoBehaviour
    {
        public CampSession Session;
        private readonly float[] _frames = new float[600];
        private readonly FrameTiming[] _timing = new FrameTiming[1];
        private int _count;
        private double _next;
        private void Update()
        {
            if (Session == null || !Session.IsTestSession || Session.View?.Self == null) return;
            bool timingEnabled = !Application.isBatchMode && FrameTimingManager.IsFeatureEnabled();
            if (timingEnabled) FrameTimingManager.CaptureFrameTimings();
            if (_count < _frames.Length) _frames[_count++] = Time.unscaledDeltaTime * 1000;
            double now = Time.realtimeSinceStartupAsDouble;
            if (now < _next || _count < 30) return;
            _next = now + 5;
            float[] sorted = new float[_count]; Array.Copy(_frames, sorted, _count); Array.Sort(sorted);
            double total = 0; foreach (float time in sorted) total += time;
            long memory;
            using (var process = System.Diagnostics.Process.GetCurrentProcess()) memory = process.WorkingSet64;
            uint timings = timingEnabled ? FrameTimingManager.GetLatestTimings(1, _timing) : 0;
            double cpu = timings > 0 ? _timing[0].cpuFrameTime : 0;
            double gpu = timings > 0 ? _timing[0].gpuFrameTime : 0;
            long graphics = UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver();
            var sample = new Snapshot { Utc = DateTime.UtcNow.ToString("O"), BatchMode = Application.isBatchMode,
                Frames = _count, AverageFps = (float)(1000 * _count / total), FrameP50Ms = sorted[_count / 2],
                FrameP95Ms = sorted[Math.Min(_count - 1, (int)(_count * .95))], FrameMaximumMs = sorted[_count - 1],
                WorkingSetBytes = memory, WorkingSetAvailable = memory > 0,
                ManagedBytes = GC.GetTotalMemory(false), Width = Screen.width, Height = Screen.height,
                LatestCpuFrameMs = cpu, LatestGpuFrameMs = gpu, CpuTimingAvailable = cpu > 0, GpuTimingAvailable = gpu > 0,
                GraphicsDriverAllocatedBytes = graphics, GraphicsAllocationAvailable = graphics > 0,
                GraphicsDevice = SystemInfo.graphicsDeviceName, GraphicsReportedCapacityMiB = SystemInfo.graphicsMemorySize };
            try { File.AppendAllText(Path.Combine(Session.TestRoot, "performance.jsonl"), JsonUtility.ToJson(sample) + "\n"); }
            catch (IOException) { }
            _count = 0;
        }
        [Serializable] private sealed class Snapshot
        {
            public string Utc;
            public bool BatchMode;
            public int Frames, Width, Height;
            public float AverageFps, FrameP50Ms, FrameP95Ms, FrameMaximumMs;
            public long WorkingSetBytes, ManagedBytes;
            public bool WorkingSetAvailable, CpuTimingAvailable, GpuTimingAvailable, GraphicsAllocationAvailable;
            public double LatestCpuFrameMs, LatestGpuFrameMs;
            // Unity graphics resource allocation estimate, not OS/dedicated VRAM usage.
            public long GraphicsDriverAllocatedBytes;
            public string GraphicsDevice;
            public int GraphicsReportedCapacityMiB;
        }
    }
}
