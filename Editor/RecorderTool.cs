using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Unity.Scripting.LifecycleManagement;
using UnityEditor;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Encoder;
using UnityEditor.Recorder.Input;
using UnityEngine;

namespace CupkekGames.Recording
{
  // One-call play-mode capture. Call a Record* method from EDIT mode: it
  // schedules play mode, starts Unity Recorder once play is up, optionally
  // drives a world-clock hour and a shot camera per recorded frame,
  // auto-stops at the frame count, and exits play mode. The session config
  // survives the play-mode domain reload via SessionState; all scene
  // mutations (temp camera, MainCamera tag, clock hold) revert with
  // play-mode exit, so there is nothing to restore.
  // partial: the [AutoStaticsCleanup] source generator emits a matching
  // declaration.
  public static partial class RecorderTool
  {
    [Serializable]
    private class Session
    {
      public string outputFile;
      public int width;
      public int height;
      public int fps;
      public int totalFrames;
      public bool gameView;
      public bool skyCam;
      public float camHeight;
      public float fov;
      public float startHour;
      public float endHour;
      public bool faceSunAtStart;
      public float yawStart;
      public float yawSweep;
      public float pitchStart;
      public float pitchEnd;
      public bool stills;
      public string stillHours;
      public string stillYaws;
      public float stillPitch;
    }

    private const string SessionKey = "CupkekGames.Recording.RecorderTool.Session";

    /// <summary>
    /// The consuming project's time-of-day system, required only for hour
    /// sweeps (startHour &gt;= 0). Register from an [InitializeOnLoadMethod]
    /// so the registration survives every domain reload. Process-lifetime
    /// registration — deliberately exempt from statics cleanup.
    /// </summary>
    [NoAutoStaticsCleanup]
    public static IRecorderWorldClock WorldClock { get; set; }

    [AutoStaticsCleanup]
    private static Session _live;

    [AutoStaticsCleanup]
    private static RecorderController _controller;

    [AutoStaticsCleanup]
    private static IRecorderWorldClock _clock;

    [AutoStaticsCleanup]
    private static Transform _cam;

    [AutoStaticsCleanup]
    private static int _startFrame;

    [AutoStaticsCleanup]
    private static double _nextArmTime;

    [AutoStaticsCleanup]
    private static double _armDeadline;

    [AutoStaticsCleanup]
    private static List<Vector2> _stillCombos;

    [AutoStaticsCleanup]
    private static int _stillIndex;

    [AutoStaticsCleanup]
    private static int _settle;

    [AutoStaticsCleanup]
    private static RenderTexture _stillRT;

    [AutoStaticsCleanup]
    private static Texture2D _stillTex;

    /// <summary>
    /// Sky showcase: a dedicated camera at (0, 30, 0) fov 62 orbits
    /// (yawStart + yawSweep over the clip) while the world hour sweeps
    /// startHour to endHour. Hours may exceed 24 (5.5 to 29.5 = one full day,
    /// wraps mod 24); startHour &lt; 0 leaves the clock alone. Pass float.NaN
    /// as yawStart to face the sun azimuth at startHour. Negative pitch looks
    /// up. outputFile is project-relative WITHOUT extension.
    /// </summary>
    public static string RecordSkyOrbit(
        float durationSec, float startHour, float endHour,
        float yawStart, float yawSweep, float pitchStart, float pitchEnd,
        string outputFile, int width = 1920, int height = 1080, int fps = 30)
    {
      var s = new Session
      {
        skyCam = true,
        camHeight = 30f,
        fov = 62f,
        startHour = startHour,
        endHour = endHour,
        faceSunAtStart = float.IsNaN(yawStart),
        yawStart = float.IsNaN(yawStart) ? 0f : yawStart,
        yawSweep = yawSweep,
        pitchStart = pitchStart,
        pitchEnd = pitchEnd,
      };
      return Schedule(s, durationSec, outputFile, width, height, fps);
    }

    /// <summary>
    /// Ground AND sky in one shot: the same orbit + hour sweep as
    /// <see cref="RecordSkyOrbit"/>, but with the camera height and FOV
    /// exposed so it can sit near eye level and pitch DOWN. That is the
    /// framing for anything that lands on the GROUND while the sky still runs
    /// its day cycle overhead. Positive pitch looks down.
    /// </summary>
    public static string RecordEnvironmentOrbit(
        float durationSec, float startHour, float endHour,
        float camHeight, float fov,
        float yawStart, float yawSweep, float pitchStart, float pitchEnd,
        string outputFile, int width = 1920, int height = 1080, int fps = 30)
    {
      var s = new Session
      {
        skyCam = true,
        camHeight = camHeight,
        fov = fov,
        startHour = startHour,
        endHour = endHour,
        faceSunAtStart = float.IsNaN(yawStart),
        yawStart = float.IsNaN(yawStart) ? 0f : yawStart,
        yawSweep = yawSweep,
        pitchStart = pitchStart,
        pitchEnd = pitchEnd,
      };
      return Schedule(s, durationSec, outputFile, width, height, fps);
    }

    /// <summary>
    /// Plain capture of the scene's own MainCamera; no clock or camera driving.
    /// </summary>
    public static string RecordMovie(float durationSec, string outputFile,
        int width = 1920, int height = 1080, int fps = 30)
    {
      var s = new Session { skyCam = false, startHour = -1f, endHour = -1f };
      return Schedule(s, durationSec, outputFile, width, height, fps);
    }

    /// <summary>
    /// Capture the Game View exactly as displayed — the shot for UI Toolkit
    /// screens and anything else that renders as an overlay rather than
    /// through a camera. No clock or camera driving.
    /// </summary>
    public static string RecordGameView(float durationSec, string outputFile,
        int width = 1920, int height = 1080, int fps = 30)
    {
      var s = new Session { gameView = true, startHour = -1f, endHour = -1f };
      return Schedule(s, durationSec, outputFile, width, height, fps);
    }

    /// <summary>
    /// Review sheet: play-mode stills WITH post-processing, shot from a CLONE
    /// of the scene MainCamera so terrain, fog and volumes frame the shot
    /// exactly like gameplay. Cross product of hours x yaws; outPrefix is
    /// project-relative WITHOUT extension. Requires a registered WorldClock.
    /// </summary>
    public static string CaptureSkySheet(string hoursCsv, string yawsCsv,
        string outPrefix, float pitch = -8f, int width = 1920, int height = 1080)
    {
      var s = new Session
      {
        stills = true,
        stillHours = hoursCsv,
        stillYaws = yawsCsv,
        stillPitch = pitch,
        startHour = 0f,
        endHour = 0f,
      };
      return Schedule(s, 0f, outPrefix, width, height, 30);
    }

    private static string Schedule(Session s, float durationSec, string outputFile,
        int width, int height, int fps)
    {
      if (EditorApplication.isPlayingOrWillChangePlaymode)
      {
        return "[CkgRecorder] REFUSED: call from edit mode (currently playing).";
      }
      if (!string.IsNullOrEmpty(SessionState.GetString(SessionKey, "")))
      {
        return "[CkgRecorder] REFUSED: a recording session is already pending.";
      }

      s.outputFile = outputFile;
      s.width = width;
      s.height = height;
      s.fps = fps;
      s.totalFrames = Mathf.Max(1, Mathf.RoundToInt(durationSec * fps));

      string dir = Path.GetDirectoryName(outputFile);
      if (!string.IsNullOrEmpty(dir))
      {
        Directory.CreateDirectory(dir);
      }
      if (!s.stills)
      {
        // Fresh-output contract: "the .mp4 exists" must mean THIS run finished.
        string mp4 = outputFile + ".mp4";
        if (File.Exists(mp4))
        {
          File.Delete(mp4);
          File.Delete(mp4 + ".meta");
        }
      }

      SessionState.SetString(SessionKey, JsonUtility.ToJson(s));
      string what = s.stills
          ? "sky sheet (" + s.stillHours + " x " + s.stillYaws + ") -> " + outputFile + "_*.png"
          : s.totalFrames + " frames -> " + outputFile + ".mp4";
      return "[CkgRecorder] scheduled " + what +
          " (entering play mode; watch console for [CkgRecorder] DONE)";
    }

    [InitializeOnLoadMethod]
    private static void Hook()
    {
      EditorApplication.playModeStateChanged += OnPlayModeChanged;
      EditorApplication.update += ArmPending;
    }

    // Play-mode entry via a retrying editor-update pump, not a one-shot
    // delayCall: delayCall's invocation list dies with any earlier subscriber's
    // exception and with domain reloads, either of which strands the session.
    private static void ArmPending()
    {
      if (Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode ||
          EditorApplication.isCompiling || EditorApplication.isUpdating)
      {
        return;
      }
      if (string.IsNullOrEmpty(SessionState.GetString(SessionKey, "")))
      {
        _armDeadline = 0.0;
        return;
      }

      double now = EditorApplication.timeSinceStartup;
      if (_armDeadline <= 0.0)
      {
        _armDeadline = now + 45.0;
      }
      else if (now > _armDeadline)
      {
        SessionState.EraseString(SessionKey);
        _armDeadline = 0.0;
        Debug.LogError("[CkgRecorder] FAILED: play mode would not start within 45s - session dropped");
        return;
      }
      if (now < _nextArmTime)
      {
        return;
      }
      _nextArmTime = now + 3.0;
      EditorApplication.EnterPlaymode();
    }

    private static void OnPlayModeChanged(PlayModeStateChange change)
    {
      if (change == PlayModeStateChange.EnteredPlayMode)
      {
        string json = SessionState.GetString(SessionKey, "");
        if (!string.IsNullOrEmpty(json))
        {
          StartSession(JsonUtility.FromJson<Session>(json));
        }
      }
      else if (change == PlayModeStateChange.ExitingPlayMode && _live != null)
      {
        // Manual stop mid-session: finalize what we have and drop the session.
        if (_live.stills)
        {
          CleanupStills("aborted early - " + _stillIndex + " stills kept");
        }
        else
        {
          Cleanup("aborted early - partial file finalized");
        }
      }
    }

    private static bool TryAcquireClock(out IRecorderWorldClock clock)
    {
      clock = null;
      if (WorldClock == null)
      {
        Fail("hour sweep requested but no world clock is registered " +
            "(set RecorderTool.WorldClock from an [InitializeOnLoadMethod])");
        return false;
      }
      if (!WorldClock.TryBeginHold())
      {
        Fail("world clock found no time-of-day system in this project/scene");
        return false;
      }
      clock = WorldClock;
      return true;
    }

    private static void StartSession(Session s)
    {
      if (s.stills)
      {
        StartStills(s);
        return;
      }
      _live = s;
      Application.runInBackground = true;

      if (s.skyCam)
      {
        Camera old = Camera.main;
        if (old != null)
        {
          old.gameObject.tag = "Untagged";
        }
        var go = new GameObject("CkgRecorderCam");
        var cam = go.AddComponent<Camera>();
        go.tag = "MainCamera";
        cam.depth = 100f;
        cam.clearFlags = CameraClearFlags.Skybox;
        cam.fieldOfView = s.fov;
        cam.farClipPlane = 400f;
        cam.allowHDR = true;
        go.transform.position = new Vector3(0f, s.camHeight, 0f);
        _cam = go.transform;
      }
      else if (!s.gameView)
      {
        _cam = Camera.main != null ? Camera.main.transform : null;
      }

      if (s.startHour >= 0f)
      {
        if (!TryAcquireClock(out _clock))
        {
          return;
        }
        if (s.faceSunAtStart)
        {
          _clock.SetHour(s.startHour, true);
          Vector3 toward = -_clock.SunDirection;
          s.yawStart = Mathf.Atan2(toward.x, toward.z) * Mathf.Rad2Deg;
        }
      }

      if (s.skyCam && _cam != null)
      {
        _cam.rotation = Quaternion.Euler(s.pitchStart, s.yawStart, 0f);
      }

      var cs = ScriptableObject.CreateInstance<RecorderControllerSettings>();
      var movie = ScriptableObject.CreateInstance<MovieRecorderSettings>();
      movie.name = "CkgRecorder";
      movie.Enabled = true;
      movie.EncoderSettings = new CoreEncoderSettings
      {
        Codec = CoreEncoderSettings.OutputCodec.MP4,
        EncodingQuality = CoreEncoderSettings.VideoEncodingQuality.High,
      };
      if (s.gameView)
      {
        movie.ImageInputSettings = new GameViewInputSettings
        {
          OutputWidth = s.width,
          OutputHeight = s.height,
        };
      }
      else
      {
        movie.ImageInputSettings = new CameraInputSettings
        {
          Source = ImageSource.MainCamera,
          OutputWidth = s.width,
          OutputHeight = s.height,
          CaptureUI = false,
        };
      }
      movie.OutputFile = s.outputFile;
      cs.AddRecorderSettings(movie);
      cs.SetRecordModeToFrameInterval(0, s.totalFrames);
      cs.FrameRate = s.fps;
      cs.CapFrameRate = true;
      _controller = new RecorderController(cs);
      _controller.PrepareRecording();
      if (!_controller.StartRecording())
      {
        Fail("RecorderController.StartRecording returned false");
        return;
      }

      _startFrame = -1;
      EditorApplication.update += Drive;
      Debug.Log("[CkgRecorder] recording " + s.totalFrames + " frames @" + s.fps +
          "fps -> " + s.outputFile + ".mp4");
    }

    // Frame-locked driver: hour and camera are functions of the captured frame
    // index, not wall time - CapFrameRate makes game time deterministic, wall
    // time is not, so a wall-clock sweep desyncs when rendering is render-bound.
    private static void Drive()
    {
      if (_live == null)
      {
        EditorApplication.update -= Drive;
        return;
      }
      if (!Application.isPlaying)
      {
        Cleanup("play mode ended outside the tool");
        return;
      }
      if (_controller == null || !_controller.IsRecording())
      {
        Finish();
        return;
      }

      if (_startFrame < 0)
      {
        _startFrame = Time.frameCount;
      }
      float t = Mathf.Clamp01((Time.frameCount - _startFrame) / (float)_live.totalFrames);

      if (_clock != null)
      {
        float hour = Mathf.Repeat(Mathf.Lerp(_live.startHour, _live.endHour, t), 24f);
        _clock.SetHour(hour, snap: true);
      }

      if (_live.skyCam && _cam != null)
      {
        float yaw = _live.yawStart + _live.yawSweep * t;
        float pitch = Mathf.Lerp(_live.pitchStart, _live.pitchEnd, t);
        _cam.rotation = Quaternion.Euler(pitch, yaw, 0f);
      }
    }

    // Stills session: clone the scene camera (keeps its additional camera
    // data component, so post-processing and volume masks ride along WITHOUT
    // this assembly referencing URP types), park it on a RenderTexture, then
    // step hour x yaw combos with settle frames so bloom/exposure land before
    // each read.
    private static void StartStills(Session s)
    {
      _live = s;
      Application.runInBackground = true;

      if (!TryAcquireClock(out _clock))
      {
        return;
      }

      Camera src = Camera.main;
      if (src == null)
      {
        Fail("sky sheet needs a scene MainCamera to clone");
        return;
      }
      var go = UnityEngine.Object.Instantiate(src.gameObject);
      go.name = "CkgRecorderSheetCam";
      go.tag = "Untagged";
      Camera cam = go.GetComponent<Camera>();
      foreach (var b in go.GetComponentsInChildren<Behaviour>(true))
      {
        bool keep = ReferenceEquals(b, cam) ||
            (b.gameObject == go && b.GetType().Name == "UniversalAdditionalCameraData");
        if (!keep)
        {
          b.enabled = false;
        }
      }
      var listener = go.GetComponentInChildren<AudioListener>();
      if (listener != null)
      {
        UnityEngine.Object.Destroy(listener);
      }
      _cam = go.transform;
      _stillRT = new RenderTexture(s.width, s.height, 24);
      cam.targetTexture = _stillRT;
      _stillTex = new Texture2D(s.width, s.height, TextureFormat.RGB24, false);

      _stillCombos = new List<Vector2>();
      foreach (string h in s.stillHours.Split(','))
      {
        foreach (string y in s.stillYaws.Split(','))
        {
          _stillCombos.Add(new Vector2(
              float.Parse(h.Trim(), CultureInfo.InvariantCulture),
              float.Parse(y.Trim(), CultureInfo.InvariantCulture)));
        }
      }
      _stillIndex = 0;
      _settle = -1;
      EditorApplication.update += DriveStills;
      Debug.Log("[CkgRecorder] sheet: " + _stillCombos.Count + " stills -> " +
          s.outputFile + "_*.png");
    }

    private static void DriveStills()
    {
      if (_live == null)
      {
        EditorApplication.update -= DriveStills;
        return;
      }
      if (!Application.isPlaying)
      {
        CleanupStills("play mode ended outside the tool");
        return;
      }
      if (_stillIndex >= _stillCombos.Count)
      {
        int n = _stillIndex;
        string prefix = _live.outputFile;
        CleanupStills(null);
        Debug.Log("[CkgRecorder] SHEET DONE - " + n + " stills -> " + prefix +
            "_*.png (exiting play mode)");
        EditorApplication.ExitPlaymode();
        return;
      }

      Vector2 combo = _stillCombos[_stillIndex];
      if (_settle < 0)
      {
        _clock.SetHour(Mathf.Repeat(combo.x, 24f), snap: true);
        _cam.rotation = Quaternion.Euler(_live.stillPitch, combo.y, 0f);
        _settle = 4;
        return;
      }
      _settle--;
      if (_settle > 0)
      {
        return;
      }

      RenderTexture prev = RenderTexture.active;
      RenderTexture.active = _stillRT;
      _stillTex.ReadPixels(new Rect(0, 0, _stillRT.width, _stillRT.height), 0, 0);
      _stillTex.Apply();
      RenderTexture.active = prev;
      string name = _live.outputFile +
          "_h" + combo.x.ToString("0.##", CultureInfo.InvariantCulture) +
          "_y" + ((int)combo.y) + ".png";
      File.WriteAllBytes(name, _stillTex.EncodeToPNG());
      _stillIndex++;
      _settle = -1;
    }

    private static void CleanupStills(string abortLog)
    {
      EditorApplication.update -= DriveStills;
      if (_stillRT != null)
      {
        UnityEngine.Object.DestroyImmediate(_stillRT);
      }
      if (_stillTex != null)
      {
        UnityEngine.Object.DestroyImmediate(_stillTex);
      }
      _stillRT = null;
      _stillTex = null;
      _stillCombos = null;
      if (_clock != null)
      {
        _clock.EndHold();
        _clock = null;
      }
      _cam = null;
      _live = null;
      SessionState.EraseString(SessionKey);
      if (abortLog != null)
      {
        Debug.LogWarning("[CkgRecorder] " + abortLog);
      }
    }

    private static void Finish()
    {
      string output = _live.outputFile;
      Cleanup(null);
      Debug.Log("[CkgRecorder] DONE -> " + output + ".mp4 (exiting play mode)");
      EditorApplication.ExitPlaymode();
    }

    private static void Fail(string why)
    {
      Cleanup(null);
      Debug.LogError("[CkgRecorder] FAILED: " + why);
      EditorApplication.ExitPlaymode();
    }

    private static void Cleanup(string abortLog)
    {
      EditorApplication.update -= Drive;
      if (_controller != null && _controller.IsRecording())
      {
        _controller.StopRecording();
      }
      _controller = null;
      if (_clock != null)
      {
        _clock.EndHold();
        _clock = null;
      }
      _cam = null;
      _live = null;
      SessionState.EraseString(SessionKey);
      if (abortLog != null)
      {
        Debug.LogWarning("[CkgRecorder] " + abortLog);
      }
    }
  }
}
