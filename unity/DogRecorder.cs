// DogRecorder.cs —— 录制 / 回放遥测（JSONL）
//
// 用途：
//   · 录一段真狗的动作，之后在没有设备的情况下回放，做演示 / 对比 / 调模型
//   · 录下来的文件是每行一条遥测 JSON，可以直接用文本工具看、也能喂给别的程序
//
// 用法：挂在 CaiyuDog 上，client 拖进去。
//       快捷键：F9 开始/停止录制，F10 开始/停止回放（面板上也有按钮）。
//
// 回放时会自动：① 关掉 DogSimulator（避免和回放抢 Transform）② 切到离线模式。

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

[DisallowMultipleComponent]
public class DogRecorder : MonoBehaviour
{
    [Header("引用")]
    public DogClient client;

    [Header("录制")]
    [Range(5f, 30f)] public float sampleHz = 20f;
    public string recordFolder = "";      // 留空 = Application.persistentDataPath

    [Header("回放")]
    [Range(0.1f, 3f)] public float playbackSpeed = 1f;
    public bool loopPlayback = false;
    [Tooltip("留空则回放最近一次录制的文件")]
    public string replayFile = "";

    [Header("快捷键")]
    public bool enableHotkeys = true;

    public bool IsRecording => _recording;
    public bool IsPlaying => _playing;
    public string LastFile => _lastFile;
    public string Status => _status;

    bool _recording;
    bool _playing;
    float _sampleAcc;
    StreamWriter _writer;
    string _lastFile = "";
    string _status = "就绪";

    // 回放数据
    readonly List<DogTelemetry> _frames = new List<DogTelemetry>();
    readonly List<long> _times = new List<long>();
    int _cursor;
    long _playClockMs;
    float _playAcc;
    DogSimulator[] _sims;
    bool _simsWereEnabled;

    void Awake()
    {
        if (client == null) client = GetComponent<DogClient>();
    }

    void Update()
    {
        if (enableHotkeys)
        {
            if (Input.GetKeyDown(KeyCode.F9)) { if (_recording) StopRecording(); else StartRecording(); }
            if (Input.GetKeyDown(KeyCode.F10)) { if (_playing) StopPlayback(); else StartPlayback(loopPlayback); }
        }

        if (_recording) TickRecord();
        if (_playing) TickPlayback();
    }

    void OnDestroy() => Cleanup();

    void OnApplicationQuit() => Cleanup();

    // ------------------------------------------------------------------ 录制

    public void StartRecording()
    {
        if (client == null) { _status = "录制失败：client 没接"; return; }
        if (_recording) return;
        if (_playing) StopPlayback();

        try
        {
            string dir = string.IsNullOrEmpty(recordFolder) ? Application.persistentDataPath : recordFolder;
            Directory.CreateDirectory(dir);
            _lastFile = Path.Combine(dir, "caiyu-rec-" + System.DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".jsonl");

            _writer = new StreamWriter(_lastFile, false);
            _recording = true;
            _sampleAcc = 0f;
            _status = "录制中 → " + _lastFile;
            Debug.Log("[菜鱼狗] 开始录制 " + _lastFile);
        }
        catch (System.Exception e)
        {
            _status = "录制失败：" + e.Message;
            Debug.LogError("[菜鱼狗] 录制失败 " + e);
        }
    }

    public void StopRecording()
    {
        if (!_recording) return;
        _recording = false;
        try { _writer?.Flush(); _writer?.Dispose(); } catch { }
        _writer = null;
        _status = "录制结束：" + Path.GetFileName(_lastFile);
        Debug.Log("[菜鱼狗] 录制结束 " + _lastFile);
    }

    void TickRecord()
    {
        if (_writer == null || client == null) return;

        _sampleAcc += Time.deltaTime;
        float period = 1f / Mathf.Max(1f, sampleHz);
        if (_sampleAcc < period) return;
        _sampleAcc = 0f;

        DogTelemetry t = client.Latest;
        if (t == null) return;

        try { _writer.WriteLine(JsonUtility.ToJson(t)); }
        catch (System.Exception e) { _status = "写入失败：" + e.Message; StopRecording(); }
    }

    // ------------------------------------------------------------------ 回放

    public void StartPlayback(bool loop = false)
    {
        if (client == null) { _status = "回放失败：client 没接"; return; }
        if (_playing) return;
        if (_recording) StopRecording();

        string file = string.IsNullOrEmpty(replayFile) ? _lastFile : replayFile;
        if (string.IsNullOrEmpty(file) || !File.Exists(file))
        {
            _status = "回放失败：找不到文件（先录一段）";
            return;
        }

        try
        {
            _frames.Clear();
            _times.Clear();
            foreach (string line in File.ReadAllLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                DogTelemetry t;
                try { t = JsonUtility.FromJson<DogTelemetry>(line); } catch { continue; }
                if (t == null) continue;
                _frames.Add(t);
                _times.Add(t.t);
            }
            if (_frames.Count == 0) { _status = "回放失败：文件里没有有效帧"; return; }

            // 回放期间必须独占：关掉模拟器，并强制离线模式（否则真机数据会打架）
            _sims = FindObjectsOfType<DogSimulator>();
            _simsWereEnabled = false;
            foreach (var s in _sims)
            {
                if (s != null && s.enabled) { s.enabled = false; _simsWereEnabled = true; }
            }
            client.offlineSimulation = true;
            client.Reconnect();

            _cursor = 0;
            _playClockMs = _times[0];
            _playAcc = 0f;
            _playing = true;
            _status = $"回放中 {_frames.Count} 帧 ×{playbackSpeed:F1}";
            Debug.Log("[菜鱼狗] 回放 " + file + $"（{_frames.Count} 帧）");
        }
        catch (System.Exception e)
        {
            _status = "回放失败：" + e.Message;
            Debug.LogError("[菜鱼狗] 回放失败 " + e);
        }
    }

    public void StopPlayback()
    {
        if (!_playing) return;
        _playing = false;

        if (_sims != null && _simsWereEnabled)
        {
            foreach (var s in _sims) if (s != null) s.enabled = true;
        }
        _sims = null;
        _status = "回放结束";
    }

    void TickPlayback()
    {
        if (client == null || _frames.Count == 0) return;

        _playAcc += Time.deltaTime;
        float period = 1f / Mathf.Max(1f, sampleHz);
        if (_playAcc < period) return;
        float dt = _playAcc;
        _playAcc = 0f;
        _playClockMs += (long)(dt * 1000f * playbackSpeed);

        // 把这一拍所有到期的帧都注入（掉帧时追赶）
        int guard = 0;
        while (_cursor < _frames.Count && _times[_cursor] <= _playClockMs && guard++ < 32)
        {
            client.InjectTelemetry(_frames[_cursor]);
            _cursor++;
        }

        if (_cursor < _frames.Count) return;

        if (loopPlayback)
        {
            _cursor = 0;
            _playClockMs = _times[0];
        }
        else
        {
            StopPlayback();
        }
    }

    void Cleanup()
    {
        if (_recording) StopRecording();
        if (_playing) StopPlayback();
    }
}
