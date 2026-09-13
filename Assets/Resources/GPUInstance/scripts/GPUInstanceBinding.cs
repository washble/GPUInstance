using UnityEngine;

/// <summary>
/// Opaque, owner-neutral access to one active GPU instance slot. A binding is
/// created by <see cref="GPUInstanceManager.TryRegister"/> and becomes invalid
/// when its owner is unregistered or its slot is released for reuse.
/// </summary>
public sealed class GPUInstanceBinding
{
    private GPUInstanceManager manager;
    private int slotIndex;

    internal GPUInstanceBinding(GPUInstanceManager manager, int slotIndex)
    {
        this.manager = manager;
        this.slotIndex = slotIndex;
    }

    /// <summary>Reusable slot index while this binding remains active; otherwise -1.</summary>
    public int SlotIndex => slotIndex;
    public bool IsValid => manager != null && slotIndex >= 0;

    public bool TryGetBoneWorldTRS(
        string boneName,
        out Vector3 position,
        out Quaternion rotation,
        out Vector3 scale)
    {
        if (manager != null)
        {
            return manager.TryGetBoneWorldTRS(this, boneName, out position, out rotation, out scale);
        }

        position = default;
        rotation = Quaternion.identity;
        scale = Vector3.one;
        return false;
    }

    public bool TrySetProceduralBoneLocalRotation(string boneName, Quaternion localRotation)
    {
        return manager != null && manager.TrySetProceduralBoneLocalRotation(this, boneName, localRotation);
    }

    public void ClearProceduralBoneLocalRotation()
    {
        manager?.ClearProceduralBoneLocalRotation(this);
    }

    public bool TrySetAnimation(string animationName, float speed, bool loop)
    {
        return manager != null && manager.TrySetAnimation(this, animationName, speed, loop);
    }

    public bool TryGetAnimationDuration(string animationName, out float durationSeconds)
    {
        if (manager != null)
        {
            return manager.TryGetAnimationDuration(this, animationName, out durationSeconds);
        }

        durationSeconds = 0f;
        return false;
    }

    internal void Invalidate()
    {
        manager = null;
        slotIndex = -1;
    }
}

/// <summary>
/// Implemented by owner-side visuals that need to observe an assigned generic
/// GPU binding without making the GPU manager responsible for their lifecycle.
/// </summary>
public interface IGpuInstanceBindingListener
{
    void BindGpuInstance(GPUInstanceBinding binding);
}
