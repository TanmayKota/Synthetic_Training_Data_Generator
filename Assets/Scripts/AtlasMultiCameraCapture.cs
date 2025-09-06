// AtlasGpuNvencCapture.cs
// Requires Unity 2019.1+ for AsyncGPUReadback; recommended >= 2020.
// Paste into your project and attach to an empty GameObject.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

[DisallowMultipleComponent]
public class AtlasMultiCameraCapture : MonoBehaviour
{
    [Header("Cameras & outputs (length must match)")]
    public Camera[] cameras = new Camera[0];
    [Tooltip("Absolute or relative (to Application.dataPath) path per camera")]
    public string[] outputPaths = new string[0];

    [Header("FFmpeg / Encoding")]
    [Tooltip("ffmpeg executable name or full path")]
    public string ffmpegPath = "ffmpeg";
    [Tooltip("Encode with NVENC (NVIDIA). If false, uses libx264 (CPU)")]
    public bool useNVENC = true;
    [Tooltip("Framerate to tell ffmpeg (frames/sec). Use your game's target frame rate.")]
    public int encodingFps = 60;
    [Tooltip("NVENC/encoding configuration: high quality smaller number better")]
    public int crfOrCQ = 18;

    [Header("Capture")]
    public bool saveAsPNG = false; // if true, stream PNG; else stream raw rgba -> ffmpeg
    [Tooltip("If true, clear existing files in outputPaths at start")]
    public bool clearFolderOnStart = false;
    [Tooltip("How often to log progress (frames)")]
    public int progressLogEveryNFrames = 300;

    // internals
    RenderTexture atlasRT;
    Rect[] cameraRects; // pixel rect per camera in atlas (bottom-left origin)
    int atlasW, atlasH;
    int[] camW, camH;
    int tileW, tileH;
    long frameIndex = 0;
    bool isRecording = false;
    byte[] atlasBuffer = null; // reused managed buffer for atlas RGBA bytes

    // ffmpeg processes & streams
    Process[] ffmpegProcs;
    StreamWriter[] ffmpegStdIns; // not used for binary; use BaseStream
    System.IO.Stream[] ffmpegStreams;

    // background write control
    List<Task> pendingWriteTasks = new List<Task>();
    readonly object pendingLock = new object();
    int maxPendingWrites = 8;

    void OnValidate()
    {
        progressLogEveryNFrames = Math.Max(1, progressLogEveryNFrames);
        encodingFps = Mathf.Clamp(encodingFps, 1, 240);
    }

    bool ValidateAndPrepare()
    {
        if (cameras == null || cameras.Length == 0)
        {
            Debug.LogError("[AtlasCapture] No cameras assigned.");
            return false;
        }
        if (outputPaths == null || outputPaths.Length < cameras.Length)
        {
            Debug.LogError("[AtlasCapture] outputPaths must match cameras length.");
            return false;
        }

        // canonicalize output paths and test write access
        for (int i = 0; i < cameras.Length; i++)
        {
            if (cameras[i] == null)
            {
                Debug.LogError($"[AtlasCapture] cameras[{i}] is null.");
                return false;
            }
            string p = outputPaths[i];
            if (string.IsNullOrWhiteSpace(p))
            {
                Debug.LogError($"[AtlasCapture] outputPaths[{i}] is empty for camera {i}.");
                return false;
            }
            string folder = p;
            if (!Path.IsPathRooted(folder))
                folder = Path.GetFullPath(Path.Combine(Application.dataPath, folder));
            try
            {
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                else if (clearFolderOnStart)
                {
                    var files = Directory.GetFiles(folder);
                    foreach (var f in files) File.Delete(f);
                }
                // write test
                string test = Path.Combine(folder, $".write_test_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(test, "ok"); File.Delete(test);
                outputPaths[i] = folder;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AtlasCapture] Cannot prepare folder '{folder}': {ex.Message}");
                return false;
            }
        }

        // ffmpeg available?
        try
        {
            var check = new Process();
            check.StartInfo.FileName = ffmpegPath;
            check.StartInfo.Arguments = "-version";
            check.StartInfo.CreateNoWindow = true;
            check.StartInfo.UseShellExecute = false;
            check.StartInfo.RedirectStandardOutput = true;
            check.Start();
            check.WaitForExit(1000);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[AtlasCapture] ffmpeg not found at '{ffmpegPath}'. Ensure ffmpeg is installed and path set. Exception: {ex.Message}");
            // allow to continue if user wants to later set ffmpeg path
        }

        return true;
    }

    void ComputeAtlasLayout()
    {
        int n = cameras.Length;
        camW = new int[n];
        camH = new int[n];
        int maxW = 0, maxH = 0;
        for (int i = 0; i < n; ++i)
        {
            int w = cameras[i].pixelWidth > 0 ? cameras[i].pixelWidth : Screen.width;
            int h = cameras[i].pixelHeight > 0 ? cameras[i].pixelHeight : Screen.height;
            camW[i] = w; camH[i] = h;
            if (w > maxW) maxW = w;
            if (h > maxH) maxH = h;
        }

        tileW = maxW; tileH = maxH;
        int cols = Mathf.CeilToInt(Mathf.Sqrt((float)n));
        int rows = Mathf.CeilToInt((float)n / cols);
        atlasW = cols * tileW;
        atlasH = rows * tileH;

        cameraRects = new Rect[n];
        for (int i = 0; i < n; ++i)
        {
            int col = i % cols;
            int row = i / cols;
            int x = col * tileW;
            int y = (rows - 1 - row) * tileH; // bottom-left origin
            // place camera image aligned bottom-left within the cell; use actual camW/camH
            cameraRects[i] = new Rect(x, y, camW[i], camH[i]);
        }

        Debug.Log($"[AtlasCapture] atlas {atlasW}x{atlasH}; tile {tileW}x{tileH}; cams {n}");
    }

    void EnsureAtlasRT()
    {
        if (atlasRT != null && atlasRT.width == atlasW && atlasRT.height == atlasH) return;
        if (atlasRT != null) { atlasRT.Release(); DestroyImmediate(atlasRT); atlasRT = null; }
        atlasRT = new RenderTexture(atlasW, atlasH, 0, RenderTextureFormat.ARGB32);
        atlasRT.useMipMap = false;
        atlasRT.Create();
        // allocate managed buffer for atlas (RGBA32)
        atlasBuffer = new byte[atlasW * atlasH * 4];
    }

    public void StartRecording()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[AtlasCapture] StartRecording only works in Play mode.");
            return;
        }
        if (!ValidateAndPrepare()) return;

        ComputeAtlasLayout();
        EnsureAtlasRT();

        // set cameras to render to atlas (we will set pixelRect per camera during render)
        foreach (var cam in cameras) cam.targetTexture = atlasRT;

        // spawn ffmpeg processes
        StartFfmpegProcesses();

        isRecording = true;
        frameIndex = 0;
        StartCoroutine(CaptureLoop());
        Debug.Log("[AtlasCapture] Started recording with GPU-accelerated pipeline.");
    }

    public void StopRecording()
    {
        if (!isRecording) return;
        isRecording = false;
    }

    IEnumerator CaptureLoop()
    {
        while (Application.isPlaying && isRecording)
        {
            yield return new WaitForEndOfFrame();

            // 1) render cameras into atlas tiles
            for (int i = 0; i < cameras.Length; ++i)
            {
                Camera cam = cameras[i];
                Rect tile = cameraRects[i];
                cam.pixelRect = new Rect(tile.x, tile.y, tile.width, tile.height);
                cam.targetTexture = atlasRT;
                cam.Render();
            }
            // reset pixelRect optionally
            for (int i = 0; i < cameras.Length; ++i)
            {
                cameras[i].pixelRect = new Rect(0, 0, cameras[i].pixelWidth, cameras[i].pixelHeight);
            }

            // 2) async GPU readback (non-blocking)
            var req = AsyncGPUReadback.Request(atlasRT, 0, TextureFormat.RGBA32, OnAtlasReadback);
            // don't WaitForCompletion here; callback will run when data ready

            frameIndex++;
            if (frameIndex % Math.Max(1, progressLogEveryNFrames) == 0)
                Debug.Log($"[AtlasCapture] rendered frames: {frameIndex}, pending writes: {pendingWriteTasks.Count}");
        }

        // wait for pending writes to finish
        Debug.Log("[AtlasCapture] Stopping: waiting pending writes...");
        Task[] waits;
        lock (pendingLock)
        {
            waits = pendingWriteTasks.ToArray();
        }
        try { Task.WaitAll(waits); } catch { /*ignore*/ }

        Cleanup();
        Debug.Log("[AtlasCapture] Stopped and cleaned up.");
    }

    void OnAtlasReadback(AsyncGPUReadbackRequest req)
    {
        if (req.hasError)
        {
            Debug.LogError("[AtlasCapture] Readback error.");
            return;
        }

        // copy to managed atlasBuffer once
        try
        {
            var nat = req.GetData<byte>();
            // nat.CopyTo(atlasBuffer) uses NativeArray.CopyTo
            nat.CopyTo(atlasBuffer);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[AtlasCapture] Error copying readback: {ex.Message}");
            return;
        }

        // for each camera slice out bytes and write to its ffmpeg stdin
        for (int i = 0; i < cameras.Length; ++i)
        {
            var rect = cameraRects[i];
            int w = (int)rect.width;
            int h = (int)rect.height;
            int tx = (int)rect.x;
            int ty = (int)rect.y;

            // reuse buffer per camera if possible; allocate if needed
            int camBytesLen = w * h * 4;
            byte[] camBuf = BufferPool.Get(camBytesLen);

            // copy row-by-row from atlasBuffer (atlas stride = atlasW*4)
            int atlasStride = atlasW * 4;
            int camStride = w * 4;
            for (int row = 0; row < h; ++row)
            {
                int srcRow = ty + row;
                int srcOffset = (srcRow * atlasW + tx) * 4;
                int dstOffset = row * camStride;
                Buffer.BlockCopy(atlasBuffer, srcOffset, camBuf, dstOffset, camStride);
            }

            // now write camBuf to ffmpeg stdin async (no blocking). ffmpeg expects raw RGBA
            if (ffmpegStreams != null && ffmpegStreams.Length > i && ffmpegStreams[i] != null)
            {
                var stream = ffmpegStreams[i];
                // Fire-and-forget write with throttling
                ScheduleStreamWrite(stream, camBuf, i);
            }
            else
            {
                // if no ffmpeg process, fallback: write PNG/JPG files (not recommended for 17 cams)
                string fallbackFile = Path.Combine(outputPaths[i], $"cam_{i}_frame_{frameIndex:D08}.raw");
                File.WriteAllBytes(fallbackFile, camBuf);
                BufferPool.Return(camBuf);
            }
        }
    }

    void ScheduleStreamWrite(System.IO.Stream stream, byte[] buf, int camIndex)
    {
        // Throttle number of pending writes
        lock (pendingLock)
        {
            pendingWriteTasks.RemoveAll(t => t.IsCompleted);
            if (pendingWriteTasks.Count > maxPendingWrites)
            {
                // too many pending; drop frame or await some tasks — here we drop oldest writes to avoid piling
                Debug.LogWarning("[AtlasCapture] too many pending writes; dropping frame to avoid stall.");
                BufferPool.Return(buf);
                return;
            }
        }

        var task = Task.Run(async () =>
        {
            try
            {
                await stream.WriteAsync(buf, 0, buf.Length);
                await stream.FlushAsync();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AtlasCapture] ffmpeg write failed (cam {camIndex}): {ex.Message}");
            }
            finally
            {
                BufferPool.Return(buf);
            }
        });

        lock (pendingLock)
        {
            pendingWriteTasks.Add(task);
        }
    }

    void StartFfmpegProcesses()
    {
        int n = cameras.Length;
        ffmpegProcs = new Process[n];
        ffmpegStdIns = new StreamWriter[n];
        ffmpegStreams = new System.IO.Stream[n];

        for (int i = 0; i < n; ++i)
        {
            int w = camW[i];
            int h = camH[i];
            string outFile = Path.Combine(outputPaths[i], $"{SanitizeFileName(cameras[i].gameObject.name)}.mp4");

            string codecArgs = useNVENC
                ? $"-c:v h264_nvenc -preset p6 -rc vbr_hq -cq {crfOrCQ}"
                : $"-c:v libx264 -preset fast -crf {crfOrCQ}";

            // ffmpeg reads rawvideo rgba from stdin
            string args = $"-y -f rawvideo -pix_fmt rgba -video_size {w}x{h} -framerate {encodingFps} -i - {codecArgs} \"{outFile}\"";

            var p = new Process();
            p.StartInfo.FileName = ffmpegPath;
            p.StartInfo.Arguments = args;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.RedirectStandardInput = true;
            p.Start();

            ffmpegProcs[i] = p;
            ffmpegStreams[i] = p.StandardInput.BaseStream;
            Debug.Log($"[AtlasCapture] Started ffmpeg for cam{i}: {args}");
        }
    }

    void Cleanup()
    {
        // stop ffmpeg & streams
        if (ffmpegStreams != null)
        {
            for (int i = 0; i < ffmpegStreams.Length; ++i)
            {
                try
                {
                    if (ffmpegStreams[i] != null)
                    {
                        ffmpegStreams[i].Flush();
                        ffmpegStreams[i].Close();
                        ffmpegStreams[i] = null;
                    }
                    if (ffmpegProcs != null && ffmpegProcs[i] != null && !ffmpegProcs[i].HasExited)
                    {
                        ffmpegProcs[i].WaitForExit(2000);
                        if (!ffmpegProcs[i].HasExited) ffmpegProcs[i].Kill();
                    }
                }
                catch (Exception ex) { Debug.LogWarning($"[AtlasCapture] ffmpeg cleanup ex: {ex.Message}"); }
            }
        }

        // release atlas
        if (atlasRT != null) { atlasRT.Release(); DestroyImmediate(atlasRT); atlasRT = null; }
        atlasBuffer = null;

        // return buffers and clear pending tasks
        lock (pendingLock)
        {
            Task[] waits = pendingWriteTasks.ToArray();
            try { Task.WaitAll(waits, 5000); } catch { }
            pendingWriteTasks.Clear();
        }

        // restore cameras
        foreach (var cam in cameras)
        {
            if (cam != null) cam.targetTexture = null;
        }
    }

    void OnDisable()
    {
        if (isRecording)
        {
            isRecording = false;
            Cleanup();
        }
    }

    void OnApplicationQuit()
    {
        if (isRecording)
        {
            isRecording = false;
            Cleanup();
        }
    }

    static string SanitizeFileName(string n)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        return n.Replace(' ', '_');
    }

    // very small buffer pool to avoid allocations
    static class BufferPool
    {
        static readonly Dictionary<int, Stack<byte[]>> pool = new Dictionary<int, Stack<byte[]>>();
        static readonly object poolLock = new object();

        public static byte[] Get(int size)
        {
            lock (poolLock)
            {
                if (!pool.TryGetValue(size, out var stack)) { stack = new Stack<byte[]>(); pool[size] = stack; }
                if (stack.Count > 0) return stack.Pop();
            }
            return new byte[size];
        }

        public static void Return(byte[] b)
        {
            lock (poolLock)
            {
                if (!pool.TryGetValue(b.Length, out var stack)) { stack = new Stack<byte[]>(); pool[b.Length] = stack; }
                stack.Push(b);
            }
        }
    }
}