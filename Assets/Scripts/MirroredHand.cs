using UnityEngine;
using Oculus.Interaction;
using Oculus.Interaction.Input;

/// <summary>
/// Synthetic IHand for the mirrored hand (mirror therapy, SCRUM-44).
///
/// Not a physically tracked hand — wraps the real IHand of the healthy hand
/// (<see cref="_sourceHand"/>) and returns, for every joint query, the
/// sagittal reflection of that same joint. Point the affected side's
/// HandVisual at this class instead of the normal tracking source: Meta's
/// HandVisual already knows how to draw every joint from an IHand, so there
/// are no bones to drive manually here.
///
/// Validity (IsConnected/IsTrackedDataValid/IsHighConfidence/Scale) always
/// follows the healthy hand — the mirrored hand doesn't physically exist, so
/// it has no tracking of its own to lose.
///
/// Handedness is fixed to Left (right→left direction hardcoded for now;
/// dynamic healthy_hand is future work, out of scope for this ticket).
/// </summary>
public class MirroredHand : MonoBehaviour, IHand
{
    [Header("Real (healthy) hand to mirror")]
    [SerializeField, Interface(typeof(IHand))] private UnityEngine.Object _sourceHand;
    private IHand SourceHand;

    [Header("Sagittal plane (Tracking Space)")]
    [Tooltip("OVRCameraRig's TrackingSpace. Same logic as MirrorHandTest: the plane is anchored to the tracking space's forward, not the head's forward.")]
    public Transform trackingSpace;
    public Transform centerEye;
    public float neckOffset = 0.12f;

    public event System.Action WhenHandUpdated;

    void Awake()
    {
        SourceHand = _sourceHand as IHand;
        if (SourceHand == null) Debug.LogError("_sourceHand does not implement IHand");

        if (trackingSpace == null && centerEye != null)
            trackingSpace = centerEye.parent;
        if (trackingSpace == null)
            Debug.LogError("trackingSpace not assigned (and not resolved via centerEye.parent).");
    }

    void OnEnable()
    {
        if (SourceHand != null) SourceHand.WhenHandUpdated += RaiseWhenHandUpdated;
    }

    void OnDisable()
    {
        if (SourceHand != null) SourceHand.WhenHandUpdated -= RaiseWhenHandUpdated;
    }

    private void RaiseWhenHandUpdated() => WhenHandUpdated?.Invoke();

    // --- Identity and validity: always the healthy hand's, never our own. ---

    public Handedness Handedness => Handedness.Left;
    public bool IsConnected => SourceHand.IsConnected;
    public bool IsHighConfidence => SourceHand.IsHighConfidence;
    public bool IsDominantHand => SourceHand.IsDominantHand;
    public float Scale => SourceHand.Scale;
    public bool IsTrackedDataValid => SourceHand.IsTrackedDataValid;
    public bool IsPointerPoseValid => SourceHand.IsPointerPoseValid;
    public int CurrentDataVersion => SourceHand.CurrentDataVersion;

    // --- Per-finger pinch/confidence: scalars with no laterality, passed straight through. ---

    public bool GetFingerIsPinching(HandFinger finger) => SourceHand.GetFingerIsPinching(finger);
    public bool GetIndexFingerIsPinching() => SourceHand.GetIndexFingerIsPinching();
    public float GetFingerPinchStrength(HandFinger finger) => SourceHand.GetFingerPinchStrength(finger);
    public bool GetFingerIsHighConfidence(HandFinger finger) => SourceHand.GetFingerIsHighConfidence(finger);

    // --- World poses: same math validated in MirrorHandTest. ---

    public bool GetJointPose(HandJointId handJointId, out Pose pose)
    {
        pose = default;
        if (!SourceHand.GetJointPose(handJointId, out Pose sourcePose)) return false;
        pose = MirrorWorldPose(sourcePose);
        return true;
    }

    public bool GetRootPose(out Pose pose) => GetJointPose(HandJointId.HandWristRoot, out pose);

    public bool GetPointerPose(out Pose pose)
    {
        pose = default;
        if (!SourceHand.GetPointerPose(out Pose sourcePose)) return false;
        pose = MirrorWorldPose(sourcePose);
        return true;
    }

    // --- Local/wrist-relative poses: HandVisual uses GetJointPosesLocal to
    // draw all 26 joints of the whole skeleton (confirmed by reading the
    // SDK's HandVisual.cs). A first version tried mirroring the real hand's
    // local pose directly (negate position, keep rotation) based on a
    // comparison against a default bind pose — but that's only a valid test
    // for the finger joints (non-trivial rest rotation); at the wrist the
    // rest rotation is nearly identical either way, so the comparison didn't
    // prove anything there, and in the headset test the wrist rotated 180°
    // wrong.
    //
    // The current approach doesn't depend on any assumption about OVR's
    // local-axis convention: it computes the mirrored WORLD pose of each of
    // the 26 joints (the same math as GetJointPose, already validated with
    // the 3 markers) and derives every "local"/"wrist-relative" pose from the
    // geometric difference between a joint and its parent joint — pure
    // geometry, with no dependency on any internal SDK convention. ---

    public bool GetJointPoseLocal(HandJointId handJointId, out Pose pose)
    {
        pose = default;
        if (!GetJointPosesLocal(out ReadOnlyHandJointPoses localPoses)) return false;
        pose = localPoses[(int)handJointId];
        return true;
    }

    public bool GetJointPosesLocal(out ReadOnlyHandJointPoses localJointPoses)
    {
        localJointPoses = ReadOnlyHandJointPoses.Empty;
        if (!TryGetAllMirroredWorldPoses(out Pose[] worldPoses)) return false;

        var local = new Pose[worldPoses.Length];
        for (int i = 0; i < worldPoses.Length; i++)
        {
            int parent = (int)HandJointUtils.JointParentList[i];
            local[i] = parent < 0 ? Pose.identity : RelativePose(worldPoses[parent], worldPoses[i]);
        }
        localJointPoses = new ReadOnlyHandJointPoses(local);
        return true;
    }

    public bool GetJointPoseFromWrist(HandJointId handJointId, out Pose pose)
    {
        pose = default;
        if (!GetJointPosesFromWrist(out ReadOnlyHandJointPoses fromWrist)) return false;
        pose = fromWrist[(int)handJointId];
        return true;
    }

    public bool GetJointPosesFromWrist(out ReadOnlyHandJointPoses jointPosesFromWrist)
    {
        jointPosesFromWrist = ReadOnlyHandJointPoses.Empty;
        if (!TryGetAllMirroredWorldPoses(out Pose[] worldPoses)) return false;

        Pose wristWorld = worldPoses[(int)HandJointId.HandWristRoot];
        var fromWrist = new Pose[worldPoses.Length];
        for (int i = 0; i < worldPoses.Length; i++)
            fromWrist[i] = RelativePose(wristWorld, worldPoses[i]);
        jointPosesFromWrist = new ReadOnlyHandJointPoses(fromWrist);
        return true;
    }

    // HandJointId.HandPalm doesn't exist in this project's active enum (it's
    // specific to the OpenXR Hands pipeline, which isn't in use here) — the
    // palm isn't a tracked joint, it's a fixed offset relative to the wrist.
    // Meta's Hand.cs resolves this with `offset = -offset` for the left
    // hand, rotation unchanged; we replicate the same rule on the real
    // hand's pose.
    public bool GetPalmPoseLocal(out Pose pose)
    {
        pose = default;
        if (!SourceHand.GetPalmPoseLocal(out Pose sourcePose)) return false;
        pose = new Pose(-sourcePose.position, sourcePose.rotation);
        return true;
    }

    private bool TryGetAllMirroredWorldPoses(out Pose[] worldPoses)
    {
        worldPoses = new Pose[Constants.NUM_HAND_JOINTS];
        for (int i = 0; i < worldPoses.Length; i++)
        {
            if (!GetJointPose((HandJointId)i, out worldPoses[i])) return false;
        }
        return true;
    }

    /// <summary>"Child" pose in "parent"'s frame of reference — standard rigid composition.</summary>
    private static Pose RelativePose(Pose parent, Pose child)
    {
        Quaternion invParentRot = Quaternion.Inverse(parent.rotation);
        return new Pose(
            invParentRot * (child.position - parent.position),
            invParentRot * child.rotation);
    }

    /// <summary>
    /// Sagittal reflection of a world pose. Identical math to the one
    /// validated in MirrorHandTest.TryComputeMirrorPose: only the local X
    /// (in the plane's frame of reference) is negated for position; the
    /// rotation reflects forward/up and rebuilds with LookRotation to
    /// preserve chirality without singularities.
    /// </summary>
    private Pose MirrorWorldPose(Pose pose)
    {
        UpdateSagittalPlane(out Quaternion planeRot, out Vector3 planePos);
        Quaternion invPlane = Quaternion.Inverse(planeRot);

        Vector3 p = invPlane * (pose.position - planePos);
        Quaternion r = invPlane * pose.rotation;

        p.x = -p.x;
        Vector3 worldPos = planePos + planeRot * p;

        Vector3 rightForward = r * Vector3.forward;
        Vector3 rightUp = r * Vector3.up;
        Vector3 leftForward = new Vector3(-rightForward.x, rightForward.y, rightForward.z);
        Vector3 leftUp = new Vector3(-rightUp.x, rightUp.y, rightUp.z);
        Quaternion worldRot = planeRot * Quaternion.LookRotation(leftForward, leftUp);

        return new Pose(worldPos, worldRot);
    }

    private void UpdateSagittalPlane(out Quaternion planeRot, out Vector3 planePos)
    {
        Vector3 tsForward = trackingSpace.forward;
        tsForward.y = 0f;
        if (tsForward.sqrMagnitude < 1e-6f) tsForward = Vector3.forward;
        tsForward.Normalize();
        planeRot = Quaternion.LookRotation(tsForward, Vector3.up);

        Vector3 frozenForward = planeRot * Vector3.forward;
        planePos = centerEye.position - frozenForward * neckOffset;
    }
}
