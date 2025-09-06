using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;

[RequireComponent(typeof(Camera))]
public class CameraRecorder : MonoBehaviour
{
    [Header("Recording")]
    public int fps = 60;
    public float durationSeconds = 10f;
    [Tooltip("Output root folder. If empty, will use Application.persistentDataPath/CameraRecordings")]
    public string outputRoot = "";
    [Tooltip("Subfolder name for this camera's run. If empty the script uses camera name + timestamp.")]
    public string runFolderName = "";

    [Header("Render & image")]
    public int captureWidth = 1280;
    public int captureHeight = 720;
    [Range(1, 100)] public int jpgQuality = 85;

    [Header("FFmpeg (optional)")]
    public bool runFfmpegAfter = true;
    [Tooltip("ffmpeg executable path or 'ffmpeg' if in PATH")]
    public string ffmpegPath = "ffmpeg";
    [Tooltip("CRF for ffmpeg encoding; lower is better quality (0-51).")]
    public int ffmpegCrf = 18;

    [Header("Behavior")]
    public bool startOnAwake = false;
    public bool overwriteExisting = false;

    // internals
    Camera cam;
    RenderTexture rt;
    bool isRecording = false;
    int totalFrames;
    int frameIndex;
    int prevCaptureFramerate;
    RenderTexture previousTargetTexture;

    void Awake()
    {
        cam = GetComponent<Camera>();
        if (string.IsNullOrEmpty(outputRoot))
            outputRoot = Path.Combine(Application.persistentDataPath, "CameraRecordings");

        if (startOnAwake && Application.isPlaying)
            StartRecording();
    }

    [ContextMenu("Start Recording")]
    public void StartRecording()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[CameraRecorder] Recording works only in Play mode.");
            return;
        }

        if (isRecording)
        {
            Debug.LogWarning("[CameraRecorder] Already recording.");
            return;
        }

        StartCoroutine(RecordCoroutine());
    }

    [ContextMenu("Stop Recording (if running)")]
    public void StopRecording()
    {
        if (!isRecording)
        {
            Debug.Log("[CameraRecorder] Not recording.");
            return;
        }
        // Stopping is accomplished by letting the coroutine finish early via flag
        isRecording = false;
    }

    IEnumerator RecordCoroutine()
    {
        // prepare
        isRecording = true;
        frameIndex = 0;
        totalFrames = Mathf.CeilToInt(durationSeconds * fps);

        // prepare folder
        string cameraName = gameObject.name.Replace(" ", "_");
        string timeStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string runName = string.IsNullOrEmpty(runFolderName) ? $"{cameraName}_{timeStamp}" : runFolderName;
        string outRunFolder = Path.Combine(outputRoot, runName);

        if (Directory.Exists(outRunFolder))
        {
            if (overwriteExisting)
            {
                Debug.Log($"[CameraRecorder] Overwriting existing folder: {outRunFolder}");
            }
            else
            {
                // find a non-conflicting folder name
                int i = 1;
                string orig = outRunFolder;
                while (Directory.Exists(outRunFolder))
                {
                    outRunFolder = orig + $"_{i:D3}";
                    i++;
                }
            }
        }

        Directory.CreateDirectory(outRunFolder);
        Debug.Log($"[CameraRecorder] Recording to: {outRunFolder}  ({totalFrames} frames @ {fps} fps)");

        // create RenderTexture
        rt = new RenderTexture(captureWidth, captureHeight, 24);
        rt.Create();

        // stash previous camera targetTexture and assign ours
        previousTargetTexture = cam.targetTexture;
        cam.targetTexture = rt;

        // store and set Time.captureFramerate to lock frame output
        prevCaptureFramerate = Time.captureFramerate;
        Time.captureFramerate = fps;

        // Main capture loop
        for (int f = 0; f < totalFrames && isRecording; f++)
        {
            yield return new WaitForEndOfFrame(); // ensure frame rendered

            // ensure camera has rendered this frame into the RT
            cam.Render();

            // read pixels
            RenderTexture currentActive = RenderTexture.active;
            RenderTexture.active = rt;

            Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();

            // encode JPG
            byte[] bytes = tex.EncodeToJPG(jpgQuality);
            UnityEngine.Object.DestroyImmediate(tex);

            // save
            string fileName = Path.Combine(outRunFolder, $"frame_{f:D06}.jpg");
            File.WriteAllBytes(fileName, bytes);

            RenderTexture.active = currentActive;

            frameIndex++;
            if (frameIndex % 60 == 0) // occasional progress log
                Debug.Log($"[CameraRecorder] {gameObject.name}: captured {frameIndex}/{totalFrames}");
        }

        // restore
        cam.targetTexture = previousTargetTexture;
        if (rt != null)
        {
            rt.Release();
            DestroyImmediate(rt);
            rt = null;
        }

        Time.captureFramerate = prevCaptureFramerate;

        isRecording = false;
        Debug.Log($"[CameraRecorder] Finished recording {gameObject.name}. Frames saved: {frameIndex}. Folder: {outRunFolder}");

        if (runFfmpegAfter && frameIndex > 0)
        {
            // build output mp4 path
            string outMp4 = Path.Combine(outRunFolder, $"{cameraName}.mp4");
            yield return StartCoroutine(RunFfmpegOnFolder(outRunFolder, outMp4));
        }
    }

    IEnumerator RunFfmpegOnFolder(string framesFolder, string outFile)
    {
        // example ffmpeg args:
        // ffmpeg -y -framerate 60 -i frame_%06d.jpg -c:v libx264 -preset fast -crf 18 -pix_fmt yuv420p out.mp4

        if (!Directory.Exists(framesFolder))
        {
            Debug.LogWarning($"[CameraRecorder] FFmpeg: frames folder doesn't exist: {framesFolder}");
            yield break;
        }

        // quick check: is ffmpeg available?
        bool ffExists = true;
        try
        {
            var check = new Process();
            check.StartInfo.FileName = ffmpegPath;
            check.StartInfo.Arguments = "-version";
            check.StartInfo.CreateNoWindow = true;
            check.StartInfo.UseShellExecute = false;
            check.StartInfo.RedirectStandardOutput = true;
            check.Start();
            check.WaitForExit(1500);
        }
        catch (Exception e)
        {
            ffExists = false;
            Debug.LogError($"[CameraRecorder] FFmpeg executable not found or failed to start: '{ffmpegPath}'. Exception: {e.Message}");
        }

        if (!ffExists)
            yield break;

        string pattern = Path.Combine(framesFolder, "frame_%06d.jpg");
        string args = $"-y -framerate {fps} -i \"{pattern}\" -c:v libx264 -preset fast -crf {ffmpegCrf} -pix_fmt yuv420p \"{outFile}\"";

        Debug.Log($"[CameraRecorder] Running ffmpeg: {ffmpegPath} {args}");

        var proc = new Process();
        proc.StartInfo.FileName = ffmpegPath;
        proc.StartInfo.Arguments = args;
        proc.StartInfo.CreateNoWindow = true;
        proc.StartInfo.UseShellExecute = false;
        proc.StartInfo.RedirectStandardOutput = true;
        proc.StartInfo.RedirectStandardError = true;

        try
        {
            proc.Start();
        }
        catch (Exception e)
        {
            Debug.LogError($"[CameraRecorder] Failed to launch ffmpeg: {e.Message}");
            yield break;
        }

        // read output until exit (non-blocking-ish)
        while (!proc.HasExited)
        {
            string outStr = proc.StandardOutput.ReadToEnd();
            string errStr = proc.StandardError.ReadToEnd();
            if (!string.IsNullOrEmpty(errStr))
                Debug.Log("[ffmpeg] " + errStr);
            yield return null;
        }

        if (proc.ExitCode == 0)
            Debug.Log($"[CameraRecorder] FFmpeg finished: {outFile}");
        else
            Debug.LogError($"[CameraRecorder] FFmpeg exited with code {proc.ExitCode}. See console output.");
    }

    void OnDisable()
    {
        // if the GameObject is destroyed / disabled while recording, cleanup neatly
        if (isRecording)
        {
            isRecording = false;
        }
        if (rt != null)
        {
            rt.Release();
            DestroyImmediate(rt);
            rt = null;
        }

        // attempt to restore Time.captureFramerate if we changed it
        Time.captureFramerate = prevCaptureFramerate;

        if (cam != null)
            cam.targetTexture = previousTargetTexture;
    }
}
