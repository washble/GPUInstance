using UnityEngine;

/// <summary>
/// Applies a non-accumulating, post-animation local rotation to one named bone
/// on this object's assigned GPU instance binding.
/// </summary>
[DisallowMultipleComponent]
public sealed class GPUProceduralBoneRotation : MonoBehaviour, IGpuInstanceBindingListener
{
    [Header("Procedural Bone Rotation")]
    [SerializeField, Tooltip("Name of the baked GPU skeleton bone to rotate, such as Body, Head, or TurretHead. A GPU binding supports one active procedural bone rotation; do not use another controller on the same binding.")]
    private string boneName;

    [SerializeField, Tooltip("Euler rotation in the selected bone's local space. It is applied after the sampled animation rotation and does not accumulate between frames.")]
    private Vector3 localRotation;

    private GPUInstanceBinding gpuBinding;
    private string appliedBoneName;
    private bool hasAppliedRotation;
    private bool isRotationActive = true;
    private string warnedBoneName;

    /// <summary>The selected baked GPU bone name.</summary>
    public string BoneName => boneName;

    /// <summary>
    /// Called by the generic GPU registration flow. A pooled owner receives a
    /// fresh binding whenever it is assigned a reusable GPU slot.
    /// </summary>
    public void BindGpuInstance(GPUInstanceBinding binding)
    {
        if (gpuBinding != binding)
        {
            ClearOwnedRotation();
            gpuBinding = binding;
            appliedBoneName = null;
            warnedBoneName = null;
        }
    }

    /// <summary>
    /// Updates the local post-animation rotation from an owner-side controller.
    /// The supplied Euler value is interpreted in the selected bone's local space.
    /// </summary>
    public void SetRuntimeLocalRotation(Vector3 rotation)
    {
        localRotation = rotation;
        isRotationActive = true;
    }

    /// <summary>
    /// Stops this component from applying a procedural rotation until a later
    /// <see cref="SetRuntimeLocalRotation"/> call, and clears its active slot value.
    /// </summary>
    public void ClearRuntimeLocalRotation()
    {
        isRotationActive = false;
        ClearOwnedRotation();
    }

    /// <summary>Gets the selected bone's reconstructed animated world-space pose.</summary>
    public bool TryGetSelectedBoneWorldTRS(
        out Vector3 position,
        out Quaternion rotation,
        out Vector3 scale)
    {
        if (gpuBinding != null && gpuBinding.IsValid && !string.IsNullOrWhiteSpace(boneName))
        {
            return gpuBinding.TryGetBoneWorldTRS(boneName, out position, out rotation, out scale);
        }

        position = default;
        rotation = Quaternion.identity;
        scale = Vector3.one;
        return false;
    }

    private void Update()
    {
        if (gpuBinding == null || !gpuBinding.IsValid)
        {
            hasAppliedRotation = false;
            appliedBoneName = null;
            return;
        }

        if (!isRotationActive)
        {
            return;
        }

        // The generic API supports one procedural bone per binding. Changing
        // the selected name must remove this component's prior selection before
        // attempting to apply the new one.
        if (hasAppliedRotation && appliedBoneName != boneName)
        {
            ClearOwnedRotation();
        }

        if (string.IsNullOrWhiteSpace(boneName))
        {
            WarnMissingBoneOnce("No Bone Name is configured.");
            return;
        }

        Quaternion rotation = Quaternion.Euler(localRotation);
        if (gpuBinding.TrySetProceduralBoneLocalRotation(boneName, rotation))
        {
            hasAppliedRotation = true;
            appliedBoneName = boneName;
            warnedBoneName = null;
            return;
        }

        WarnMissingBoneOnce($"The assigned GPU model has no bone named '{boneName}'.");
    }

    private void OnDisable()
    {
        ClearOwnedRotation();
    }

    private void OnDestroy()
    {
        ClearOwnedRotation();
    }

    private void ClearOwnedRotation()
    {
        if (hasAppliedRotation && gpuBinding != null && gpuBinding.IsValid)
        {
            gpuBinding.ClearProceduralBoneLocalRotation();
        }

        hasAppliedRotation = false;
        appliedBoneName = null;
    }

    private void WarnMissingBoneOnce(string reason)
    {
        if (warnedBoneName == boneName)
        {
            return;
        }

        Debug.LogWarning($"{nameof(GPUProceduralBoneRotation)} on '{name}' cannot apply its rotation: {reason}", this);
        warnedBoneName = boneName;
    }
}
