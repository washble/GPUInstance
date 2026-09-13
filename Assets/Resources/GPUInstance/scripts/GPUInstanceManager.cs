using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// The world-space presentation settings for one gameplay-prefab to GPU Render
/// Prefab mapping. This value is owned by the gameplay system that chooses a
/// render model, rather than by the GPU instance manager, because different
/// models can need different mesh alignment, scale, and culling bounds.
/// </summary>
[System.Serializable]
public sealed class GPUInstanceVisualTransform
{
    [SerializeField] private Vector3 positionOffset = Vector3.zero;
    [SerializeField] private Vector3 rotationOffset = new Vector3(-90f, 180f, 0f);
    [SerializeField, Min(0.0001f)] private float scale = 100f;
    [SerializeField, Min(0f)] private float cullingRadius = 4f;

    public Vector3 PositionOffset => positionOffset;
    public Quaternion RotationOffset => Quaternion.Euler(rotationOffset);
    public float Scale => scale;
    public float CullingRadius => cullingRadius;
    public bool IsValid => scale > 0f && cullingRadius >= 0f;
}

/// <summary>
/// Owns GPU Animation Instancing resources and maps arbitrary active Unity
/// component owners to reusable GPU skinned-mesh slots.
/// </summary>
[DisallowMultipleComponent]
public sealed class GPUInstanceManager : MonoBehaviour
{
    private const int TransformDirtyFlags =
        GPUInstance.DirtyFlag.Position | GPUInstance.DirtyFlag.Rotation;
    [FormerlySerializedAs("FrustumCullingCamera")]
    [SerializeField] private Camera frustumCullingCamera;

    [Header("GPU Slot Pool")]
    [SerializeField, Min(1)] private int maximumSlotCount = 300;

    private readonly Dictionary<Component, GPUInstanceSlot> activeSlotsByOwner =
        new Dictionary<Component, GPUInstanceSlot>();
    private readonly List<GPUInstanceSlot> allocatedSlots = new List<GPUInstanceSlot>();
    private readonly Dictionary<GPUInstance.GPUSkinnedMeshComponent, Stack<GPUInstanceSlot>> freeSlotsByGpuModel =
        new Dictionary<GPUInstance.GPUSkinnedMeshComponent, Stack<GPUInstanceSlot>>();
    private readonly List<GPUInstanceSlot> pendingReleaseSlots = new List<GPUInstanceSlot>();
    private readonly List<Component> staleOwners = new List<Component>();
    private readonly List<GPUInstance.GPUSkinnedMeshComponent> configuredGpuModels =
        new List<GPUInstance.GPUSkinnedMeshComponent>();

    private GPUInstance.MeshInstancer meshInstancer;
    private ComputeBuffer proceduralBoneAimBuffer;
    private ProceduralBoneAimData[] proceduralBoneAimData;
    private bool initialized;
    private int lastGpuUpdateFrame = -1;

    /// <summary>Number of active owner-to-GPU-slot mappings.</summary>
    public int ActiveInstanceCount => activeSlotsByOwner.Count;

    /// <summary>Total initialized GPU slots retained by this manager.</summary>
    public int AllocatedSlotCount => allocatedSlots.Count;

    /// <summary>
    /// Raised after the current GPU animation frame has been evaluated.
    /// Binding-owned effects use this to read their assigned slot's current pose.
    /// </summary>
    public event Action GpuAnimationUpdated;

    /// <summary>Initialized slots ready to be assigned during a future frame.</summary>
    public int FreeSlotCount
    {
        get
        {
            int count = 0;
            foreach (Stack<GPUInstanceSlot> slots in freeSlotsByGpuModel.Values)
            {
                count += slots.Count;
            }

            return count;
        }
    }

    private enum SlotState
    {
        Active,
        PendingRelease,
        Free
    }

    /// <summary>
    /// Reference type is intentional: <see cref="GPUInstance.SkinnedMesh"/> is a
    /// mutable struct and must not be copied while a slot is assigned.
    /// </summary>
    private sealed class GPUInstanceSlot
    {
        public readonly int SlotIndex;
        public readonly GPUInstance.GPUSkinnedMeshComponent GpuRenderModel;
        public GPUInstance.SkinnedMesh Instance;
        public GPUInstanceVisualTransform VisualTransform;
        public Component Owner;
        public Transform SourceTransform;
        public GPUInstanceBinding Binding;
        public SlotState State;
        public bool InitialUploadPending = true;
        public int ProceduralBoneIndex = -1;
        public Quaternion ProceduralBoneLocalRotation = Quaternion.identity;

        public GPUInstanceSlot(
            int slotIndex,
            GPUInstance.GPUSkinnedMeshComponent gpuRenderModel,
            GPUInstance.SkinnedMesh instance)
        {
            SlotIndex = slotIndex;
            GpuRenderModel = gpuRenderModel;
            Instance = instance;
            State = SlotState.Free;
        }
    }

    /// <summary>
    /// One optional post-animation local rotation per assigned gameplay slot.
    /// The matching HLSL layout is intentionally compact and independent of any
    /// particular mech or bone name.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProceduralBoneAimData
    {
        public Vector4 LocalRotation;
        public int BoneIndex;
        public int Enabled;
        public int Padding0;
        public int Padding1;

        public static ProceduralBoneAimData Disabled => new ProceduralBoneAimData
        {
            LocalRotation = new Vector4(0f, 0f, 0f, 1f),
            BoneIndex = -1,
            Enabled = 0
        };
    }

    /// <summary>
    /// Sets the baked GPU Render Prefabs that this manager can render. The prefabs
    /// are only used as mesh and animation sources; they are never instantiated.
    /// </summary>
    public bool ConfigureGpuRenderPrefabs(IEnumerable<GPUInstance.GPUSkinnedMeshComponent> gpuRenderModels)
    {
        if (initialized)
        {
            Debug.LogError($"{nameof(GPUInstanceManager)} cannot change GPU Render Prefabs after initialization.", this);
            return false;
        }

        configuredGpuModels.Clear();
        if (gpuRenderModels == null)
        {
            return false;
        }

        foreach (GPUInstance.GPUSkinnedMeshComponent gpuRenderModel in gpuRenderModels)
        {
            if (gpuRenderModel != null && !configuredGpuModels.Contains(gpuRenderModel))
            {
                configuredGpuModels.Add(gpuRenderModel);
            }
        }

        return configuredGpuModels.Count > 0;
    }

    private bool EnsureInitialized()
    {
        if (initialized)
        {
            return true;
        }

        if (configuredGpuModels.Count == 0)
        {
            Debug.LogError($"{nameof(GPUInstanceManager)} requires at least one mapped GPU Render Prefab.", this);
            return false;
        }

        try
        {
            int boneCount = 0;
            int hierarchyDepth = 0;
            List<GPUAnimation.AnimationController> animationControllers =
                new List<GPUAnimation.AnimationController>();

            for (int index = 0; index < configuredGpuModels.Count; index++)
            {
                GPUInstance.GPUSkinnedMeshComponent gpuRenderModel = configuredGpuModels[index];
                if (gpuRenderModel == null || gpuRenderModel.anim == null)
                {
                    throw new System.Exception("A configured GPU Render Prefab is missing its animation controller.");
                }

                if (!gpuRenderModel.anim.IsIntialized)
                {
                    gpuRenderModel.anim.Initialize();
                }
                boneCount = Mathf.Max(boneCount, gpuRenderModel.anim.BoneCount);
                hierarchyDepth = Mathf.Max(hierarchyDepth, gpuRenderModel.anim.BoneHierarchyDepth + 2);

                if (!animationControllers.Contains(gpuRenderModel.anim))
                {
                    animationControllers.Add(gpuRenderModel.anim);
                }
            }

            meshInstancer = new GPUInstance.MeshInstancer();
            meshInstancer.Initialize(
                num_skeleton_bones: boneCount,
                max_parent_depth: hierarchyDepth);

            proceduralBoneAimData = new ProceduralBoneAimData[maximumSlotCount];
            for (int index = 0; index < proceduralBoneAimData.Length; index++)
            {
                proceduralBoneAimData[index] = ProceduralBoneAimData.Disabled;
            }

            proceduralBoneAimBuffer = new ComputeBuffer(
                maximumSlotCount,
                Marshal.SizeOf<ProceduralBoneAimData>());
            proceduralBoneAimBuffer.SetData(proceduralBoneAimData);
            meshInstancer.SetProceduralBoneAimBuffer(proceduralBoneAimBuffer);
            meshInstancer.SetAllAnimations(animationControllers.ToArray());

            for (int index = 0; index < configuredGpuModels.Count; index++)
            {
                meshInstancer.AddGPUSkinnedMeshType(
                    configuredGpuModels[index],
                    override_shadows: true,
                    shadow_mode: UnityEngine.Rendering.ShadowCastingMode.On,
                    receive_shadows: true);
            }

            initialized = true;
            return true;
        }
        catch (System.Exception exception)
        {
            Debug.LogException(exception, this);
            meshInstancer?.Dispose();
            meshInstancer = null;
            proceduralBoneAimBuffer?.Release();
            proceduralBoneAimBuffer = null;
            proceduralBoneAimData = null;
            return false;
        }
    }

    /// <summary>
    /// Acquires a reusable GPU slot for an arbitrary gameplay owner. The caller
    /// supplies the owner separately from the Transform that drives the visual,
    /// allowing any gameplay type to use the same GPU infrastructure.
    /// </summary>
    public bool TryRegister(
        Component owner,
        Transform sourceTransform,
        GPUInstance.GPUSkinnedMeshComponent gpuRenderModel,
        GPUInstanceVisualTransform visualTransform,
        out GPUInstanceBinding binding)
    {
        binding = null;
        if (owner == null || sourceTransform == null || gpuRenderModel == null || visualTransform == null || !visualTransform.IsValid ||
            !configuredGpuModels.Contains(gpuRenderModel))
        {
            return false;
        }

        if (!EnsureInitialized())
        {
            return false;
        }

        if (activeSlotsByOwner.TryGetValue(owner, out GPUInstanceSlot existingSlot))
        {
            Debug.LogWarning($"'{owner.name}' already has a GPU slot mapping.", owner);
            binding = existingSlot.Binding;
            return true;
        }

        if (!TryAcquireSlot(gpuRenderModel, visualTransform, out GPUInstanceSlot slot))
        {
            Debug.LogError(
                $"{nameof(GPUInstanceManager)} reached its GPU slot capacity ({maximumSlotCount}).",
                this);
            return false;
        }

        try
        {
            slot.Owner = owner;
            slot.SourceTransform = sourceTransform;
            slot.Binding = new GPUInstanceBinding(this, slot.SlotIndex);
            slot.VisualTransform = visualTransform;
            slot.State = SlotState.Active;
            ConfigureSlotForBinding(slot);
            activeSlotsByOwner.Add(owner, slot);
            binding = slot.Binding;
            NotifyBindingListeners(owner, binding);
            return true;
        }
        catch (System.Exception exception)
        {
            Debug.LogException(exception, owner);
            activeSlotsByOwner.Remove(owner);
            QueueSlotForRelease(slot);
            return false;
        }
    }

    /// <summary>
    /// Removes the logical mapping immediately. The GPU slot is hidden at this
    /// manager's update boundary and retained for later reuse.
    /// </summary>
    public void Unregister(Component owner)
    {
        if (owner == null || !activeSlotsByOwner.Remove(owner, out GPUInstanceSlot slot))
        {
            return;
        }

        QueueSlotForRelease(slot);
    }

    /// <summary>
    /// Gets an animated bone's current world-space pose for one registered binding.
    /// The pose includes the assigned slot's visual transform and animation time.
    /// </summary>
    internal bool TryGetBoneWorldTRS(
        GPUInstanceBinding binding,
        string boneName,
        out Vector3 position,
        out Quaternion rotation,
        out Vector3 scale)
    {
        position = default;
        rotation = Quaternion.identity;
        scale = Vector3.one;

        if (!TryGetActiveSlot(binding, out GPUInstanceSlot slot) || string.IsNullOrWhiteSpace(boneName) ||
            !slot.GpuRenderModel.anim.namedBones.TryGetValue(boneName, out int boneIndex))
        {
            return false;
        }

        slot.Instance.BoneWorldTRS(
            boneIndex,
            slot.ProceduralBoneIndex,
            slot.ProceduralBoneLocalRotation,
            out position,
            out rotation,
            out scale);
        return true;
    }

    /// <summary>
    /// Applies one additive local rotation after the sampled animation pose for a
    /// named bone on this binding's own GPU slot.
    /// </summary>
    internal bool TrySetProceduralBoneLocalRotation(
        GPUInstanceBinding binding,
        string boneName,
        Quaternion localRotation)
    {
        if (!TryGetActiveSlot(binding, out GPUInstanceSlot slot) || string.IsNullOrWhiteSpace(boneName) ||
            !slot.GpuRenderModel.anim.namedBones.TryGetValue(boneName, out int boneIndex))
        {
            return false;
        }

        localRotation = Quaternion.Normalize(localRotation);
        if (slot.ProceduralBoneIndex == boneIndex &&
            Mathf.Abs(Quaternion.Dot(slot.ProceduralBoneLocalRotation, localRotation)) > 0.999999f)
        {
            return true;
        }

        slot.ProceduralBoneIndex = boneIndex;
        slot.ProceduralBoneLocalRotation = localRotation;
        SetProceduralBoneAimData(slot, enabled: true);
        return true;
    }

    /// <summary>Removes the optional post-animation rotation from this binding's slot.</summary>
    internal void ClearProceduralBoneLocalRotation(GPUInstanceBinding binding)
    {
        if (!TryGetActiveSlot(binding, out GPUInstanceSlot slot) ||
            slot.ProceduralBoneIndex < 0)
        {
            return;
        }

        slot.ProceduralBoneIndex = -1;
        slot.ProceduralBoneLocalRotation = Quaternion.identity;
        SetProceduralBoneAimData(slot, enabled: false);
    }

    /// <summary>
    /// Selects a named baked animation for one mapped binding. Names are
    /// resolved against that binding's assigned GPU render model, never by a shared
    /// numeric index.
    /// </summary>
    internal bool TrySetAnimation(
        GPUInstanceBinding binding,
        string animationName,
        float speed,
        bool loop)
    {
        if (!TryGetActiveSlot(binding, out GPUInstanceSlot slot) || string.IsNullOrWhiteSpace(animationName))
        {
            return false;
        }

        GPUAnimation.AnimationController animationController = slot.GpuRenderModel.anim;
        if (animationController == null)
        {
            return false;
        }

        GPUAnimation.Animation[] animations = animationController.animations;
        if (animations == null)
        {
            return false;
        }

        for (int index = 0; index < animations.Length; index++)
        {
            GPUAnimation.Animation animation = animations[index];
            if (animation == null || !string.Equals(animation.name, animationName, System.StringComparison.Ordinal))
            {
                continue;
            }

            slot.Instance.SetAnimation(animation, speed, start_time: 0f, loop: loop);
            return true;
        }

        Debug.LogWarning(
            $"{nameof(GPUInstanceManager)} could not find animation '{animationName}' for '{slot.Owner.name}'.",
            slot.Owner);
        return false;
    }

    /// <summary>
    /// Gets a named baked animation's duration for one binding's assigned render
    /// model. This remains model-agnostic so gameplay can schedule a visual
    /// animation cycle from the data the GPU instance actually plays.
    /// </summary>
    internal bool TryGetAnimationDuration(
        GPUInstanceBinding binding,
        string animationName,
        out float durationSeconds)
    {
        durationSeconds = 0f;

        if (!TryGetActiveSlot(binding, out GPUInstanceSlot slot) || string.IsNullOrWhiteSpace(animationName))
        {
            return false;
        }

        GPUAnimation.AnimationController animationController = slot.GpuRenderModel.anim;
        if (animationController == null)
        {
            return false;
        }

        GPUAnimation.Animation[] animations = animationController.animations;
        if (animations == null)
        {
            return false;
        }

        for (int index = 0; index < animations.Length; index++)
        {
            GPUAnimation.Animation animation = animations[index];
            if (animation == null || !string.Equals(animation.name, animationName, System.StringComparison.Ordinal) ||
                animation.boneAnimations == null)
            {
                continue;
            }

            for (int boneIndex = 0; boneIndex < animation.boneAnimations.Length; boneIndex++)
            {
                GPUAnimation.BoneAnimation boneAnimation = animation.boneAnimations[boneIndex];
                if (boneAnimation != null && boneAnimation.AnimationLengthSeconds > 0f)
                {
                    durationSeconds = boneAnimation.AnimationLengthSeconds;
                    return true;
                }
            }

            return false;
        }

        return false;
    }

    private void LateUpdate()
    {
        if (!initialized)
        {
            return;
        }

        QueueStaleSlotsForRelease();
        UploadPendingSlotReleases();
        SynchronizeActiveSlots();

        meshInstancer.FrustumCamera = frustumCullingCamera;
        meshInstancer.Update(Time.deltaTime);
        GpuAnimationUpdated?.Invoke();
        lastGpuUpdateFrame = Time.frameCount;

        FinalizeSlotReleases();
    }

    private bool TryAcquireSlot(
        GPUInstance.GPUSkinnedMeshComponent gpuRenderModel,
        GPUInstanceVisualTransform visualTransform,
        out GPUInstanceSlot slot)
    {
        if (freeSlotsByGpuModel.TryGetValue(gpuRenderModel, out Stack<GPUInstanceSlot> freeSlots) &&
            freeSlots.Count > 0)
        {
            slot = freeSlots.Pop();
            return true;
        }

        // A release made before this frame's dispatch has not reached the GPU yet.
        // Reclaiming it here replaces its pending hide with the new visible state in
        // the same delta upload, rather than allocating an unnecessary overflow slot.
        if (lastGpuUpdateFrame != Time.frameCount && pendingReleaseSlots.Count > 0)
        {
            for (int pendingIndex = pendingReleaseSlots.Count - 1; pendingIndex >= 0; pendingIndex--)
            {
                GPUInstanceSlot pendingSlot = pendingReleaseSlots[pendingIndex];
                if (pendingSlot.GpuRenderModel != gpuRenderModel)
                {
                    continue;
                }

                pendingReleaseSlots.RemoveAt(pendingIndex);
                pendingSlot.State = SlotState.Free;
                slot = pendingSlot;
                return true;
            }
        }

        if (allocatedSlots.Count >= maximumSlotCount)
        {
            slot = null;
            return false;
        }

        return TryCreateSlot(gpuRenderModel, visualTransform, out slot);
    }

    private bool TryCreateSlot(
        GPUInstance.GPUSkinnedMeshComponent gpuRenderModel,
        GPUInstanceVisualTransform visualTransform,
        out GPUInstanceSlot slot)
    {
        GPUInstance.SkinnedMesh instance = default;
        try
        {
            instance = new GPUInstance.SkinnedMesh(gpuRenderModel, meshInstancer);
            instance.SetRadius(visualTransform.CullingRadius);
            SetSlotVisibility(ref instance, invisible: true);
            instance.Initialize();

            slot = new GPUInstanceSlot(allocatedSlots.Count, gpuRenderModel, instance);
            slot.VisualTransform = visualTransform;
            allocatedSlots.Add(slot);
            return true;
        }
        catch (System.Exception exception)
        {
            Debug.LogException(exception, this);
            if (instance.Initialized())
            {
                instance.Dispose();
            }

            slot = null;
            return false;
        }
    }

    private void ConfigureSlotForBinding(GPUInstanceSlot slot)
    {
        slot.ProceduralBoneIndex = -1;
        slot.ProceduralBoneLocalRotation = Quaternion.identity;
        slot.Instance.mesh.props_pad2 = slot.SlotIndex + 1;
        slot.Instance.mesh.DirtyFlags |= GPUInstance.DirtyFlag.props_pad2;
        SetProceduralBoneAimData(slot, enabled: false);
        CopyTransform(slot.SourceTransform, slot.VisualTransform, ref slot.Instance);
        slot.Instance.mesh.DirtyFlags |= TransformDirtyFlags | GPUInstance.DirtyFlag.Scale;
        slot.Instance.SetRadius(slot.VisualTransform.CullingRadius);
        SetSlotVisibility(ref slot.Instance, invisible: false);

        GPUAnimation.Animation[] animations = slot.GpuRenderModel.anim.animations;
        if (animations == null || animations.Length == 0)
        {
            throw new System.Exception("The GPU animation controller has no animations.");
        }

    }

    private void QueueSlotForRelease(GPUInstanceSlot slot)
    {
        if (slot == null || slot.State == SlotState.PendingRelease || slot.State == SlotState.Free)
        {
            return;
        }

        slot.Binding?.Invalidate();
        slot.Binding = null;
        slot.Owner = null;
        slot.SourceTransform = null;
        slot.ProceduralBoneIndex = -1;
        slot.ProceduralBoneLocalRotation = Quaternion.identity;
        SetProceduralBoneAimData(slot, enabled: false);
        slot.State = SlotState.PendingRelease;
        SetSlotVisibility(ref slot.Instance, invisible: true);
        pendingReleaseSlots.Add(slot);
    }

    private void QueueStaleSlotsForRelease()
    {
        staleOwners.Clear();
        foreach (KeyValuePair<Component, GPUInstanceSlot> pair in activeSlotsByOwner)
        {
            GPUInstanceSlot slot = pair.Value;
            if (pair.Key == null || slot.SourceTransform == null ||
                !pair.Key.gameObject.activeInHierarchy || !slot.SourceTransform.gameObject.activeInHierarchy)
            {
                staleOwners.Add(pair.Key);
            }
        }

        for (int index = 0; index < staleOwners.Count; index++)
        {
            Component owner = staleOwners[index];
            if (activeSlotsByOwner.Remove(owner, out GPUInstanceSlot slot))
            {
                QueueSlotForRelease(slot);
            }
        }
    }

    private void UploadPendingSlotReleases()
    {
        for (int index = 0; index < pendingReleaseSlots.Count; index++)
        {
            GPUInstanceSlot slot = pendingReleaseSlots[index];
            if (slot.State == SlotState.PendingRelease)
            {
                // UpdateMesh uploads the root and all renderable submeshes.
                slot.Instance.UpdateMesh();
            }
        }
    }

    private void SynchronizeActiveSlots()
    {
        foreach (GPUInstanceSlot slot in activeSlotsByOwner.Values)
        {
            CopyTransform(slot.SourceTransform, slot.VisualTransform, ref slot.Instance);

            if (slot.InitialUploadPending)
            {
                slot.Instance.UpdateAll();
                slot.InitialUploadPending = false;
            }
            else
            {
                slot.Instance.mesh.DirtyFlags |= TransformDirtyFlags;
                // UpdateMesh also flushes any submesh visibility/radius changes.
                slot.Instance.UpdateMesh();
            }
        }
    }

    private void FinalizeSlotReleases()
    {
        for (int index = 0; index < pendingReleaseSlots.Count; index++)
        {
            GPUInstanceSlot slot = pendingReleaseSlots[index];
            if (slot.State != SlotState.PendingRelease)
            {
                continue;
            }

            slot.State = SlotState.Free;
            GetFreeSlotStack(slot.GpuRenderModel).Push(slot);
        }

        pendingReleaseSlots.Clear();
    }

    private Stack<GPUInstanceSlot> GetFreeSlotStack(GPUInstance.GPUSkinnedMeshComponent gpuRenderModel)
    {
        if (!freeSlotsByGpuModel.TryGetValue(gpuRenderModel, out Stack<GPUInstanceSlot> freeSlots))
        {
            freeSlots = new Stack<GPUInstanceSlot>();
            freeSlotsByGpuModel.Add(gpuRenderModel, freeSlots);
        }

        return freeSlots;
    }

    private static void CopyTransform(
        Transform source,
        GPUInstanceVisualTransform visualTransform,
        ref GPUInstance.SkinnedMesh instance)
    {
        instance.mesh.position = source.position + visualTransform.PositionOffset;
        instance.mesh.rotation = source.rotation * visualTransform.RotationOffset;
        instance.mesh.scale = Vector3.one * visualTransform.Scale;
    }

    private bool TryGetActiveSlot(GPUInstanceBinding binding, out GPUInstanceSlot slot)
    {
        slot = null;
        if (binding == null || binding.SlotIndex < 0 || binding.SlotIndex >= allocatedSlots.Count)
        {
            return false;
        }

        GPUInstanceSlot candidate = allocatedSlots[binding.SlotIndex];
        if (candidate.State != SlotState.Active || candidate.Binding != binding)
        {
            return false;
        }

        slot = candidate;
        return true;
    }

    private static void NotifyBindingListeners(Component owner, GPUInstanceBinding binding)
    {
        Component[] components = owner.GetComponents<Component>();
        for (int index = 0; index < components.Length; index++)
        {
            if (components[index] is IGpuInstanceBindingListener listener)
            {
                listener.BindGpuInstance(binding);
            }
        }
    }

    private void SetProceduralBoneAimData(GPUInstanceSlot slot, bool enabled)
    {
        if (proceduralBoneAimBuffer == null || proceduralBoneAimData == null ||
            slot == null || slot.SlotIndex < 0 || slot.SlotIndex >= proceduralBoneAimData.Length)
        {
            return;
        }

        proceduralBoneAimData[slot.SlotIndex] = enabled
            ? new ProceduralBoneAimData
            {
                LocalRotation = new Vector4(
                    slot.ProceduralBoneLocalRotation.x,
                    slot.ProceduralBoneLocalRotation.y,
                    slot.ProceduralBoneLocalRotation.z,
                    slot.ProceduralBoneLocalRotation.w),
                BoneIndex = slot.ProceduralBoneIndex,
                Enabled = 1
            }
            : ProceduralBoneAimData.Disabled;
        proceduralBoneAimBuffer.SetData(proceduralBoneAimData, slot.SlotIndex, slot.SlotIndex, 1);
    }

    private static void SetSlotVisibility(ref GPUInstance.SkinnedMesh instance, bool invisible)
    {
        instance.mesh.Invisible = invisible;
        instance.mesh.DirtyFlags |= GPUInstance.DirtyFlag.Data1;

        if (instance.sub_mesh == null)
        {
            return;
        }

        for (int index = 0; index < instance.sub_mesh.Length; index++)
        {
            instance.sub_mesh[index].Invisible = invisible;
            instance.sub_mesh[index].DirtyFlags |= GPUInstance.DirtyFlag.Data1;
        }
    }

    private void OnDestroy()
    {
        foreach (GPUInstanceSlot slot in allocatedSlots)
        {
            if (slot.Instance.Initialized())
            {
                slot.Instance.Dispose();
            }
        }

        foreach (GPUInstanceSlot slot in allocatedSlots)
        {
            slot.Binding?.Invalidate();
        }

        activeSlotsByOwner.Clear();
        allocatedSlots.Clear();
        freeSlotsByGpuModel.Clear();
        pendingReleaseSlots.Clear();
        staleOwners.Clear();
        configuredGpuModels.Clear();
        initialized = false;
        meshInstancer?.Dispose();
        meshInstancer = null;
        proceduralBoneAimBuffer?.Release();
        proceduralBoneAimBuffer = null;
        proceduralBoneAimData = null;
    }
}
