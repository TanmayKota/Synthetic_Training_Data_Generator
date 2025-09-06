// MultiCameraManagerImageSaver_ExactPaths_NoFPS_AsyncWrites.cs
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

[DisallowMultipleComponent]
public class MultiCameraManagerImageSaver : MonoBehaviour
{
    [Header("Cameras & exact output paths (one per camera)")]
    public Camera[] cameras = new Camera[0];

    [Tooltip("Output folder per camera. MUST match cameras length. If relative, resolved against Application.dataPath.")]
    public string[] outputPaths = new string[0];

    [Header("Capture")]
    public int fps = 60;
    public int captureWidth = 1280;
    public int captureHeight = 720;
    public bool saveAsPNG = false;
    [Range(1, 100)] public int jpgQuality = 85;

    [Header("Behaviour")]
    public bool startOnAwake = false;
    [Tooltip("If true, deletes existing files in the folder before starting. If false, frames are appended.")]
    public bool overwriteExisting = false;
    public int progressLogEveryNFrames = 300;

    // internals
    RenderTexture[] rts;
    Texture2D[] readTextures;
    bool isRecording = false;
    long frameIndex = 0;
    string[] resolvedFolders;

    // globals for capture framerate management
    static bool captureFramerateManaged = false;
    static int originalCaptureFramerate = 0;
    static int activeManagerCount = 0;

    void Awake()
    {
        if (outputPaths == null) outputPaths = new string[0];
        if (cameras == null) cameras = new Camera[0];

        if (startOnAwake && Application.isPlaying)
            StartAll();
    }

    void OnValidate()
    {
        fps = Mathf.Clamp(fps, 1, 240);
        captureWidth = Mathf.Max(16, captureWidth);
        captureHeight = Mathf.Max(16, captureHeight);
    }

    bool ValidateAndPrepareFolders()
    {
        if (cameras == null || cameras.Length == 0)
        {
            Debug.LogError("[ExactPaths] No cameras assigned.");
            return false;
        }
        if (outputPaths == null || outputPaths.Length < cameras.Length)
        {
            Debug.LogError("[ExactPaths] outputPaths length must be >= cameras length and contain one entry per camera.");
            return false;
        }

        resolvedFolders = new string[cameras.Length];

        for (int i = 0; i < cameras.Length; i++)
        {
            if (cameras[i] == null)
            {
                Debug.LogError($"[ExactPaths] cameras[{i}] is null. Assign all Camera components.");
                return false;
            }
            string p = outputPaths[i];
            if (string.IsNullOrWhiteSpace(p))
            {
                Debug.LogError($"[ExactPaths] outputPaths[{i}] is empty. Provide a valid path for camera {i} ({cameras[i].gameObject.name}).");
                return false;
            }

            // Resolve relative -> absolute (relative to Application.dataPath)
            string folder = p;
            if (!Path.IsPathRooted(folder))
                folder = Path.GetFullPath(Path.Combine(Application.dataPath, folder));

            try
            {
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                    Debug.Log($"[ExactPaths] Created folder: {folder}");
                }
                else
                {
                    // folder exists
                    if (overwriteExisting)
                    {
                        // delete files only (do not delete folder)
                        var files = Directory.GetFiles(folder);
                        foreach (var f in files)
                        {
                            try { File.Delete(f); } catch { /* ignore per-file deletion errors */ }
                        }
                        Debug.Log($"[ExactPaths] Cleared files in existing folder: {folder}");
                    }
                    // If overwriteExisting==false, we append into existing folder (no renaming).
                }

                // test write permission: try creating and deleting a tiny temp file
                string testFile = Path.Combine(folder, $".write_test_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ExactPaths] Cannot prepare folder '{folder}' for camera index {i}: {ex.Message}");
                return false;
            }

            resolvedFolders[i] = folder;
        }

        return true;
    }

    [ContextMenu("Start All Recording")]
    public void StartAll()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[ExactPaths] StartAll only works in Play mode.");
            return;
        }
        if (isRecording)
        {
            Debug.LogWarning("[ExactPaths] Already recording.");
            return;
        }

        if (!ValidateAndPrepareFolders()) return;

        // create RTs/textures and assign to cameras
        rts = new RenderTexture[cameras.Length];
        readTextures = new Texture2D[cameras.Length];

        for (int i = 0; i < cameras.Length; i++)
        {
            Camera cam = cameras[i];
            RenderTexture rt = new RenderTexture(captureWidth, captureHeight, 24);
            rt.Create();
            rts[i] = rt;

            readTextures[i] = new Texture2D(captureWidth, captureHeight, TextureFormat.RGB24, false);

            // assign RT to camera
            cam.targetTexture = rt;
        }

        // manage Time.captureFramerate globally
        if (!captureFramerateManaged)
        {
            originalCaptureFramerate = Time.captureFramerate;
            captureFramerateManaged = true;
        }
        activeManagerCount++;
        Time.captureFramerate = fps;

        isRecording = true;
        frameIndex = 0;
        StartCoroutine(CaptureLoop());
        Debug.Log($"[ExactPaths] Started recording {cameras.Length} cameras at {fps} fps. Using provided folders exactly as given.");
    }

    [ContextMenu("Stop All Recording")]
    public void StopAll()
    {
        if (!isRecording)
        {
            Debug.Log("[ExactPaths] Not recording.");
            return;
        }
        isRecording = false;
    }

    IEnumerator CaptureLoop()
    {
        while (Application.isPlaying && isRecording)
        {
            yield return new WaitForEndOfFrame();

            for (int i = 0; i < cameras.Length; i++)
            {
                Camera cam = cameras[i];
                string folder = resolvedFolders[i];
                if (cam == null || string.IsNullOrEmpty(folder)) continue;

                RenderTexture rt = rts[i];
                Texture2D tex = readTextures[i];

                cam.Render();

                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = rt;

                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();

                byte[] bytes;
                string ext;
                if (saveAsPNG)
                {
                    bytes = tex.EncodeToPNG();
                    ext = "png";
                }
                else
                {
                    bytes = tex.EncodeToJPG(jpgQuality);
                    ext = "jpg";
                }

                string camName = SanitizeFileName(cam.gameObject.name);
                string filePath = Path.Combine(folder, $"{camName}_frame_{frameIndex:D08}.{ext}");
                try
                {
                    File.WriteAllBytes(filePath, bytes);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[ExactPaths] Failed to write '{filePath}': {e.Message}");
                    // do not attempt to write elsewhere; continue capturing (user can StopAll)
                }

                RenderTexture.active = prev;
            }

            frameIndex++;
            if (frameIndex % Math.Max(1, progressLogEveryNFrames) == 0)
                Debug.Log($"[ExactPaths] Captured frame {frameIndex} (cameras: {cameras.Length})");
        }

        CleanupAfterStop();
        yield break;
    }

    void CleanupAfterStop()
    {
        for (int i = 0; i < cameras.Length; i++)
        {
            Camera cam = cameras[i];
            if (cam != null) cam.targetTexture = null;

            if (rts != null && i < rts.Length && rts[i] != null)
            {
                rts[i].Release();
                DestroyImmediate(rts[i]);
                rts[i] = null;
            }

            if (readTextures != null && i < readTextures.Length && readTextures[i] != null)
            {
                DestroyImmediate(readTextures[i]);
                readTextures[i] = null;
            }
        }

        rts = null;
        readTextures = null;
        resolvedFolders = null;

        activeManagerCount = Math.Max(0, activeManagerCount - 1);
        if (activeManagerCount == 0 && captureFramerateManaged)
        {
            Time.captureFramerate = originalCaptureFramerate;
            captureFramerateManaged = false;
            Debug.Log("[ExactPaths] Restored original Time.captureFramerate.");
        }

        isRecording = false;
        Debug.Log($"[ExactPaths] Stopped recording. Total frames captured: {frameIndex}");
    }

    void OnDisable()
    {
        if (isRecording)
        {
            isRecording = false;
            CleanupAfterStop();
        }
    }

    void OnApplicationQuit()
    {
        if (isRecording)
        {
            isRecording = false;
            CleanupAfterStop();
        }
    }

    static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Replace(" ", "_");
    }
}
