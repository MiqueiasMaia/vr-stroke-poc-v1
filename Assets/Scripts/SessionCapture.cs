using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using Oculus.Interaction;
using Oculus.Interaction.Input;

/// <summary>
/// Exploratory session dump: head + both real (tracked) hands, one JSON line
/// per frame. Deliberately narrow scope — no controllers, eye/face tracking,
/// Scene API, or system telemetry (unavailable, irrelevant, or out of scope
/// for the study, see docs/technical/sdk-signal-inventory.md).
///
/// Always captures both hands with their REAL tracked pose, never the
/// mirrored version from <see cref="MirroredHand"/> — the affected hand's
/// real pose can never be discarded (project hard constraint).
///
/// Trigger: a text command over local TCP ("START"/"STOP"), sent from the
/// researcher's computer — no physical controller is used. Tried and
/// discarded: any controller (left or right) marks that side as "active
/// input source = controller" for the scene's grab interactors (Building
/// Blocks), even with Simultaneous Hands and Controllers enabled — this
/// disables that hand until a strong hand gesture "reclaims" the input. TCP
/// avoids this problem entirely.
/// </summary>
public class SessionCapture : MonoBehaviour
{
    private enum HealthyHand { Unset = 0, Left, Right }

    [Header("Healthy side (required, no default value)")]
    [SerializeField] private HealthyHand healthyHand = HealthyHand.Unset;

    [Header("Head")]
    public Transform centerEye;

    [Header("Real (tracked) hands, NOT the mirrored hand")]
    [SerializeField, Interface(typeof(IHand))] private UnityEngine.Object _leftHand;
    [SerializeField, Interface(typeof(IHand))] private UnityEngine.Object _rightHand;
    private IHand LeftHand;
    private IHand RightHand;

    [Header("Trigger (researcher's network command, no physical controller)")]
    public int commandPort = 9999;

    private bool _recording;
    private int _frameIndex;
    private float _sessionStartTime;
    private string _sessionDir;
    private StreamWriter _writer;
    private readonly StringBuilder _sb = new StringBuilder(4096);

    private TcpListener _listener;
    private Thread _listenerThread;
    private volatile bool _stopListenerThread;
    private volatile bool _pendingStart;
    private volatile bool _pendingStop;

    void Awake()
    {
        LeftHand = _leftHand as IHand;
        RightHand = _rightHand as IHand;
        if (LeftHand == null) Debug.LogError("_leftHand does not implement IHand");
        if (RightHand == null) Debug.LogError("_rightHand does not implement IHand");

        if (healthyHand == HealthyHand.Unset)
            Debug.LogError("healthyHand not configured — capture cannot start without this information.");
    }

    void OnEnable()
    {
        _stopListenerThread = false;
        _listenerThread = new Thread(ListenLoop) { IsBackground = true };
        _listenerThread.Start();
    }

    void OnDisable()
    {
        _stopListenerThread = true;
        try { _listener?.Stop(); } catch { /* may already be stopped */ }
        _listenerThread?.Join(500);
    }

    /// <summary>
    /// Runs on a separate thread. Accepts short TCP connections, reads one
    /// command line ("START"/"STOP", case-insensitive), and sets a flag
    /// consumed in the main thread's Update() — never touches Unity state
    /// directly here.
    /// </summary>
    private void ListenLoop()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, commandPort);
            _listener.Start();
        }
        catch (Exception e)
        {
            Debug.LogError($"[SessionCapture] Failed to open port {commandPort}: {e.Message}");
            return;
        }

        while (!_stopListenerThread)
        {
            TcpClient client;
            try
            {
                client = _listener.AcceptTcpClient();
            }
            catch (SocketException)
            {
                break; // listener.Stop() called in OnDisable
            }

            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string line = reader.ReadLine();
                if (line == null) continue;
                line = line.Trim().ToUpperInvariant();
                if (line == "START") _pendingStart = true;
                else if (line == "STOP") _pendingStop = true;
            }
        }
    }

    void Update()
    {
        if (_pendingStart)
        {
            _pendingStart = false;
            if (!_recording) TryStartRecording();
        }
        if (_pendingStop)
        {
            _pendingStop = false;
            if (_recording) StopRecording();
        }
    }

    void LateUpdate()
    {
        if (!_recording) return;
        if (!centerEye || LeftHand == null || RightHand == null) return;

        WriteFrame();
    }

    private void TryStartRecording()
    {
        if (healthyHand == HealthyHand.Unset)
        {
            Debug.LogError("Capture refused: healthyHand not configured.");
            return;
        }

        string sessionId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)
            + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
        _sessionDir = Path.Combine(Application.persistentDataPath, "captures", sessionId);
        Directory.CreateDirectory(_sessionDir);

        WriteManifest(sessionId, isStart: true);

        // UTF8Encoding(false): no BOM. Encoding.UTF8 would write a BOM at
        // the start of the file, breaking the JSON parser on the first line.
        _writer = new StreamWriter(Path.Combine(_sessionDir, "frames.jsonl"), append: false, new UTF8Encoding(false));
        _frameIndex = 0;
        _sessionStartTime = Time.realtimeSinceStartup;
        _recording = true;

        Debug.Log($"[SessionCapture] Capture started: {_sessionDir}");
    }

    private void StopRecording()
    {
        _recording = false;
        _writer?.Flush();
        _writer?.Dispose();
        _writer = null;

        WriteManifest(null, isStart: false);

        Debug.Log($"[SessionCapture] Capture stopped. {_frameIndex} frames in {_sessionDir}");
    }

    private void WriteManifest(string sessionId, bool isStart)
    {
        string path = Path.Combine(_sessionDir, "manifest.json");
        if (isStart)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append($"\"session_id\":\"{sessionId}\",");
            sb.Append($"\"healthy_hand\":\"{healthyHand}\",");
            sb.Append($"\"start_utc\":\"{DateTime.UtcNow:o}\",");
            sb.Append($"\"device_model\":\"{Escape(SystemInfo.deviceModel)}\",");
            sb.Append($"\"app_version\":\"{Escape(Application.version)}\"");
            sb.Append('}');
            File.WriteAllText(path, sb.ToString());
        }
        else
        {
            string existing = File.Exists(path) ? File.ReadAllText(path) : "{}";
            string updated = existing.TrimEnd().TrimEnd('}')
                + $",\"end_utc\":\"{DateTime.UtcNow:o}\",\"frame_count\":{_frameIndex}}}";
            File.WriteAllText(path, updated);
        }
    }

    private static string Escape(string s) => (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

    private void WriteFrame()
    {
        _sb.Clear();
        _sb.Append('{');

        _sb.Append("\"t\":").Append(F(Time.realtimeSinceStartup - _sessionStartTime)).Append(',');
        _sb.Append("\"frame\":").Append(_frameIndex).Append(',');

        _sb.Append("\"head\":{\"p\":");
        AppendVec3(centerEye.position);
        _sb.Append(",\"q\":");
        AppendQuat(centerEye.rotation);
        _sb.Append("},");

        _sb.Append("\"left\":");
        AppendHand(LeftHand);
        _sb.Append(',');

        _sb.Append("\"right\":");
        AppendHand(RightHand);

        _sb.Append('}');
        _writer.WriteLine(_sb.ToString());

        _frameIndex++;
    }

    private void AppendHand(IHand hand)
    {
        bool valid = hand.IsTrackedDataValid;
        _sb.Append("{\"valid\":").Append(valid ? "true" : "false");
        _sb.Append(",\"high_conf\":").Append(hand.IsHighConfidence ? "true" : "false");
        _sb.Append(",\"scale\":").Append(F(hand.Scale));
        _sb.Append(",\"joints\":[");

        int n = Constants.NUM_HAND_JOINTS;
        for (int i = 0; i < n; i++)
        {
            if (i > 0) _sb.Append(',');
            if (valid && hand.GetJointPose((HandJointId)i, out Pose pose))
            {
                _sb.Append('[');
                _sb.Append(F(pose.position.x)).Append(',').Append(F(pose.position.y)).Append(',').Append(F(pose.position.z)).Append(',');
                _sb.Append(F(pose.rotation.x)).Append(',').Append(F(pose.rotation.y)).Append(',').Append(F(pose.rotation.z)).Append(',').Append(F(pose.rotation.w));
                _sb.Append(']');
            }
            else
            {
                _sb.Append("null");
            }
        }
        _sb.Append("]}");
    }

    private void AppendVec3(Vector3 v)
    {
        _sb.Append('[').Append(F(v.x)).Append(',').Append(F(v.y)).Append(',').Append(F(v.z)).Append(']');
    }

    private void AppendQuat(Quaternion q)
    {
        _sb.Append('[').Append(F(q.x)).Append(',').Append(F(q.y)).Append(',').Append(F(q.z)).Append(',').Append(F(q.w)).Append(']');
    }

    private static string F(float v) => v.ToString("F5", CultureInfo.InvariantCulture);

    void OnApplicationQuit()
    {
        if (_recording) StopRecording();
    }

    void OnDestroy()
    {
        _writer?.Dispose();
        _stopListenerThread = true;
        try { _listener?.Stop(); } catch { /* may already be stopped */ }
    }
}
