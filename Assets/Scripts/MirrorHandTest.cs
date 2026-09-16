using UnityEngine;
using UnityEngine.Serialization;
using Oculus.Interaction.Input;

/// <summary>
/// Mirror Visual Feedback effector for post-stroke motor rehabilitation research.
///
/// The right hand is tracked; <see cref="wristMarker"/>, <see cref="indexTipMarker"/>
/// and <see cref="thumbTipMarker"/> each render as the pure sagittal reflection
/// of their corresponding joint, standing in for the affected (left) hand's
/// wrist, index tip, and thumb tip. This is a
/// mathematical mirror only — no magnetic assistance, no object coupling, no
/// convergence toward anything in the scene. If the real hand misses a target's
/// height or depth, the virtual hand must reproduce that same error: any
/// correction here would corrupt the proprioceptive illusion the therapy
/// depends on and invalidate whatever motor data the session produces.
/// </summary>
public class MirrorHandTest : MonoBehaviour
{
    [Header("References")]
    public Transform centerEye;                    // CenterEyeAnchor (head)
    [SerializeField] private MonoBehaviour _hand;   // component that implements IHand (e.g. Hand on RightHandAnchor)

    [Header("Markers (drag the scene objects)")]
    public Transform wristMarker;      // HandJointId.HandWristRoot
    public Transform indexTipMarker;   // HandJointId.HandIndexTip
    public Transform thumbTipMarker;   // HandJointId.HandThumbTip

    [Header("Sagittal plane (Tracking Space)")]
    [Tooltip("OVRCameraRig's TrackingSpace. The plane's orientation is rigidly anchored to its forward (global tracking space), staying immune to gaze yaw/pitch.")]
    public Transform trackingSpace;                 // OVRCameraRig/TrackingSpace
    [Tooltip("Distance (m) from the head to the sagittal plane (mirror's Z depth).")]
    public float neckOffset = 0.12f;

    [Header("Effector stability")]
    [Tooltip("Jump (m) in the head's world position that indicates a recenter (Meta button) or a momentary tracking loss; forces the effector to snap instead of sliding across the scene.")]
    public float recenterPosThreshold = 0.2f;
    [FormerlySerializedAs("couplingSmoothTime")]
    [Tooltip("Smoothing (s) of the effector's final world position (Vector3.SmoothDamp). Removes high-frequency hand-tracking jitter without introducing perceptible lag.")]
    public float positionSmoothTime = 0.15f;

    private IHand Hand;

    private Quaternion planeRot;
    private Vector3 planePos;
    private readonly Vector3[] _velocities = new Vector3[3];
    private readonly bool[] _initialized = new bool[3];
    private Vector3 _prevHeadPos;

    void Awake()
    {
        Hand = _hand as IHand;
        if (Hand == null) Debug.LogError("_hand does not implement IHand");

        // Automatic fallback resolution: if the trackingSpace field is left
        // empty in the Inspector, we use centerEye's parent (which is the
        // rig's TrackingSpace).
        if (trackingSpace == null && centerEye != null)
            trackingSpace = centerEye.parent;
        if (trackingSpace == null)
            Debug.LogError("trackingSpace not assigned (and not resolved via centerEye.parent). Assign OVRCameraRig/TrackingSpace.");
    }

    void OnValidate()
    {
        neckOffset = Mathf.Max(0f, neckOffset);
        recenterPosThreshold = Mathf.Max(0f, recenterPosThreshold);
        positionSmoothTime = Mathf.Max(0f, positionSmoothTime);
    }

    void LateUpdate()
    {
        if (!centerEye || Hand == null || !trackingSpace) return;
        if (!Hand.IsTrackedDataValid) return;

        UpdateSagittalPlane();
        bool forceSnap = UpdateRecenterSnap();

        UpdateMarker(0, HandJointId.HandWristRoot, wristMarker, forceSnap);
        UpdateMarker(1, HandJointId.HandIndexTip, indexTipMarker, forceSnap);
        UpdateMarker(2, HandJointId.HandThumbTip, thumbTipMarker, forceSnap);
    }

    /// <summary>
    /// Mirrors a specific joint of the real hand and applies the result to
    /// the destination Transform. If the joint can't be read this frame, the
    /// marker simply keeps its last pose — the other markers aren't
    /// affected.
    /// </summary>
    private void UpdateMarker(int slot, HandJointId jointId, Transform marker, bool forceSnap)
    {
        if (!marker) return;
        if (!TryComputeMirrorPose(jointId, out Vector3 desiredPos, out Quaternion desiredRot)) return;

        if (!_initialized[slot] || forceSnap)
        {
            marker.position = desiredPos;
            _initialized[slot] = true;
        }
        else
        {
            marker.position = Vector3.SmoothDamp(marker.position, desiredPos, ref _velocities[slot], positionSmoothTime);
        }

        marker.rotation = desiredRot;
    }

    /// <summary>
    /// Pure sagittal mirroring of a joint: reflects the position (only the
    /// local X inverted; Y/Z untouched) and the rotation (reflection of the
    /// forward/up vectors, rebuilt with LookRotation to produce a proper
    /// rotation with correctly inverted chirality, instead of negating
    /// quaternion components directly).
    /// </summary>
    private bool TryComputeMirrorPose(HandJointId jointId, out Vector3 worldPos, out Quaternion worldRot)
    {
        worldPos = default;
        worldRot = default;
        if (!Hand.GetJointPose(jointId, out Pose pose)) return false;

        Quaternion invPlane = Quaternion.Inverse(planeRot);

        // Right hand's point/rotation in the plane's local space (sagittal
        // plane = local YZ plane; the local X axis is the reflection normal).
        Vector3 p = invPlane * (pose.position - planePos);
        Quaternion r = invPlane * pose.rotation;

        // Y (height) and Z (depth) pass through with NO modification — the
        // virtual hand must reproduce exactly the same reach error as the
        // real hand, never converge onto or "snap" to anything in the scene.
        p.x = -p.x;
        worldPos = planePos + planeRot * p;

        Vector3 rightForward = r * Vector3.forward;
        Vector3 rightUp = r * Vector3.up;
        Vector3 leftForward = new Vector3(-rightForward.x, rightForward.y, rightForward.z);
        Vector3 leftUp = new Vector3(-rightUp.x, rightUp.y, rightUp.z);
        worldRot = planeRot * Quaternion.LookRotation(leftForward, leftUp);
        return true;
    }

    /// <summary>
    /// The plane is rigidly aligned to the Tracking Space's forward (the
    /// rig's global tracking space), not the head's forward — turning the
    /// neck (centerEye's yaw/pitch) neither tilts nor rotates the plane. The
    /// origin (Z depth) uses the head's position, so the plane stays in
    /// front of the patient regardless of where they are in the room.
    /// </summary>
    private void UpdateSagittalPlane()
    {
        Vector3 tsForward = trackingSpace.forward;
        tsForward.y = 0f;
        if (tsForward.sqrMagnitude < 1e-6f) tsForward = Vector3.forward;
        tsForward.Normalize();
        planeRot = Quaternion.LookRotation(tsForward, Vector3.up);

        Vector3 frozenForward = planeRot * Vector3.forward;
        planePos = centerEye.position - frozenForward * neckOffset;
    }

    /// <summary>
    /// A recenter (Meta button) or a momentary tracking loss produces a
    /// discontinuous jump in the head's world position. We detect that jump
    /// and flag a forced snap for all markers on the next valid frame,
    /// instead of letting SmoothDamp visibly drag them across the scene
    /// until they reach the new target.
    /// </summary>
    private bool UpdateRecenterSnap()
    {
        float headJump = (centerEye.position - _prevHeadPos).sqrMagnitude;
        bool jumped = headJump > recenterPosThreshold * recenterPosThreshold;
        _prevHeadPos = centerEye.position;
        return jumped;
    }
}
