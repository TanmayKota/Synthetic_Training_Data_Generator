using System;
using System.Collections;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;

[RequireComponent(typeof(Camera))]
public class CameraImageSaverSharedFolder : MonoBehaviour
{
    [Header("Capture settings")]
    public int fps = 60;
    public int captureWidth = 1280;
    public int captureHeight = 720;
    [Tooltip("Save PNG (lossless) if true; otherwise JPG.")]
    public bool saveAsPNG = false;
    [Range(1, 100)] public int jpgQuality = 85;

    [Header("Output (shared)")]
    [Tooltip("Root output folder. If empty uses Application.persistentDataPath/CameraRecordings")]
    public string outputRoot = "";
    [Tooltip("If true, will overwrite the run folder if it already exists; otherwise a suffix is appended.")]
    public bool overwriteExisting = false;

    [Header("Behaviour")]
    public bool startOnAwake = false;
    public int progressLogEveryNFrames = 300; // how often to log progress

    // internals
    Camera cam;
    RenderTexture rt;
    Texture2D readTexture;
    bool isRecording = false;
    long frameIndex = 0;

    // static globals to manage Time.captureFramerate and shared run folder across instances
    static int activeRecorderCount = 0;
    static int originalCaptureFramerate = 0;
    static bool captureFramerateManaged = false;

    // shared run folder info
    static bool sharedFolderInitialized = false;
    static string sharedRunFolderPath = "";
    static readonly object folderLock = new object();

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
            Debug.LogWarning("[CameraImageSaverSharedFolder] StartRecording works only in Play mode.");
            return;
        }

        if (isRecording)
        {
            Debug.LogWarning("[CameraImageSaverSharedFolder] Already recording.");
            return;
        }

        // ensure shared run folder exists (created by the first recorder that starts)
        InitializeSharedRunFolder();

        // prepare RT and readTexture (reused)
        rt = new RenderTexture(captureWidth, captureHeight, 24);
        rt.Create();
        readTexture = new Texture2D(captureWidth, captureHeight, TextureFormat.RGB24, false);

        // hook camera to RT
        cam.targetTexture = rt;

        // manage global Time.captureFramerate (only set original first time)
        if (!captureFramerateManaged)
        {
            originalCaptureFramerate = Time.captureFramerate;
            captureFramerateManaged = true;
        }
        activeRecorderCount++;
        Time.captureFramerate = fps;

        isRecording = true;
        frameIndex = 0;

        StartCoroutine(CaptureLoop());
        Debug.Log($"[CameraImageSaverSharedFolder] Started recording {gameObject.name} -> {sharedRunFolderPath} at {fps} fps.");
    }

    [ContextMenu("Stop Recording")]
    public void StopRecording()
    {
        if (!isRecording)
        {
            Debug.Log("[CameraImageSaverSharedFolder] Not recording.");
            return;
        }

        // stopping coroutine will lead to CleanUpAndRestore being called
        isRecording = false;
    }

    IEnumerator CaptureLoop()
    {
        while (Application.isPlaying && isRecording)
        {
            yield return new WaitForEndOfFrame();

            // render camera into RT
            cam.Render();

            // read RT into Texture2D
            RenderTexture currentActive = RenderTexture.active;
            RenderTexture.active = rt;

            readTexture.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            readTexture.Apply();

            // encode
            byte[] bytes;
            string ext;
            if (saveAsPNG)
            {
                bytes = readTexture.EncodeToPNG();
                ext = "png";
            }
            else
            {
                bytes = readTexture.EncodeToJPG(jpgQuality);
                ext = "jpg";
            }

            // file name: cameraName_frameIndex
            string cameraName = SanitizeFileName(gameObject.name);
            string filePath = Path.Combine(sharedRunFolderPath, $"{cameraName}_frame_{frameIndex:D08}.{ext}");

            try
            {
                File.WriteAllBytes(filePath, bytes);
            }
            catch (Exception e)
            {
                Debug.LogError($"[CameraImageSaverSharedFolder] Failed to write frame {frameIndex}: {e.Message}");
                // continue; user may stop if persistent I/O errors occur
            }

            RenderTexture.active = currentActive;

            frameIndex++;
            if (frameIndex % Math.Max(1, progressLogEveryNFrames) == 0)
                Debug.Log($"[CameraImageSaverSharedFolder] {gameObject.name}: captured {frameIndex} frames so far.");
        }

        // when loop exits (stop requested or play stopped), cleanup
        CleanUpAndRestore();
        yield break;
    }

    void CleanUpAndRestore()
    {
        // detach RT
        if (cam != null)
            cam.targetTexture = null;

        // release textures
        if (rt != null)
        {
            rt.Release();
            DestroyImmediate(rt);
            rt = null;
        }
        if (readTexture != null)
        {
            DestroyImmediate(readTexture);
            readTexture = null;
        }

        // manage global capture framerate restoration
        activeRecorderCount = Math.Max(0, activeRecorderCount - 1);
        if (activeRecorderCount == 0 && captureFramerateManaged)
        {
            Time.captureFramerate = originalCaptureFramerate;
            captureFramerateManaged = false;
            Debug.Log("[CameraImageSaverSharedFolder] Restored original Time.captureFramerate.");
        }

        isRecording = false;
        Debug.Log($"[CameraImageSaverSharedFolder] Stopped recording {gameObject.name}. Frames saved: {frameIndex}.");
    }

    void InitializeSharedRunFolder()
    {
        if (sharedFolderInitialized) return;

        lock (folderLock)
        {
            if (sharedFolderInitialized) return;

            // create base output root if missing
            if (!Directory.Exists(outputRoot))
                Directory.CreateDirectory(outputRoot);

            // create a timestamped run folder inside outputRoot
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string candidate = Path.Combine(outputRoot, $"run_{timestamp}");

            if (Directory.Exists(candidate))
            {
                if (overwriteExisting)
                {
                    // clear it (use with caution)
                    try
                    {
                        Directory.Delete(candidate, true);
                        Directory.CreateDirectory(candidate);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[CameraImageSaverSharedFolder] Could not overwrite existing folder: {e.Message}");
                        // fall back to unique naming
                        candidate = MakeUniqueFolderName(candidate);
                    }
                }
                else
                {
                    candidate = MakeUniqueFolderName(candidate);
                }
            }
            else
            {
                Directory.CreateDirectory(candidate);
            }

            sharedRunFolderPath = candidate;
            sharedFolderInitialized = true;
            Debug.Log($"[CameraImageSaverSharedFolder] Shared run folder created: {sharedRunFolderPath}");
        }
    }

    static string MakeUniqueFolderName(string basePath)
    {
        int i = 1;
        string tryPath = basePath;
        while (Directory.Exists(tryPath))
        {
            tryPath = basePath + $"_{i:D3}";
            i++;
        }
        Directory.CreateDirectory(tryPath);
        return tryPath;
    }

    string SanitizeFileName(string name)
    {
        // remove characters not valid for filenames
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Replace(" ", "_");
    }

    void OnDisable()
    {
        if (isRecording)
        {
            isRecording = false;
            CleanUpAndRestore();
        }
    }

    void OnApplicationQuit()
    {
        if (isRecording)
        {
            isRecording = false;
            CleanUpAndRestore();
        }
    }

    // convenience: start recording programmatically
    public void StartRecordingProgrammatic()
    {
        StartRecording();
    }

    // convenience: get current frame count written
    public long GetFramesCaptured() => frameIndex;
}
