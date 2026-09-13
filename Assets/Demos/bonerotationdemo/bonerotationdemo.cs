using GPUInstance;
using System.Collections.Generic;
using UnityEngine;

namespace GPUInstanceTest
{
    /// <summary>Orchestrates the GPU bone-aim demo without owning GPU resources.</summary>
    [DefaultExecutionOrder(-100)]
    public sealed class bonerotationdemo : MonoBehaviour
    {
        private const int GridWidth = 16;
        private const int GridDepth = 16;
        private const float GridSpacing = 6f;
        // The grid spans 90 units along its forward axis.  This places its
        // nearest row 9 units ahead of Target rather than surrounding it.
        private const float GridForwardOffset = 54f;
        private const string BodyBoneConfiguration = "{\"boneName\":\"Body\"}";
        // Inspection of mech_gpu's baked Body transform shows that its local +Z
        // is the forward/aiming axis.
        private static readonly Vector3 BodyAimAxis = Vector3.forward;

        [Header("GPU Instance Setup")]
        [SerializeField] private GPUInstanceManager gpuInstanceManager;
        [SerializeField] private GPUSkinnedMeshComponent mechGpu;
        [SerializeField] private string animationName = "WalkInPlace";
        [SerializeField] private GPUInstanceVisualTransform visualTransform = new GPUInstanceVisualTransform();

        [Header("Stationary Target")]
        [SerializeField] private Transform target;

        private sealed class DemoInstance
        {
            public GPUProceduralBoneRotation Rotation;
        }

        private readonly List<DemoInstance> instances = new List<DemoInstance>(GridWidth * GridDepth);
        private Transform instanceContainer;

        private void Awake()
        {
            if (gpuInstanceManager == null)
                gpuInstanceManager = GetComponent<GPUInstanceManager>();

            if (gpuInstanceManager == null || mechGpu == null || target == null)
            {
                Debug.LogError("BoneRotationDemo requires its manager, mech_gpu, and Target.", this);
                enabled = false;
                return;
            }

            gpuInstanceManager.ConfigureGpuRenderPrefabs(new[] { mechGpu });
            CreateGrid();
        }

        private void Update()
        {
            for (int index = 0; index < instances.Count; index++)
            {
                DemoInstance instance = instances[index];
                if (instance.Rotation == null)
                    continue;

                // The public pose query includes an active procedural rotation.
                // Clear through the existing component API before querying so the
                // returned Body pose is the sampled Root -> Pelvis -> Body pose.
                // This runs before the component's own Update, which reapplies
                // the newly calculated local rotation in the same frame.
                instance.Rotation.ClearRuntimeLocalRotation();
                if (
                    !instance.Rotation.TryGetSelectedBoneWorldTRS(
                        out Vector3 bodyPosition, out Quaternion bodyWorldRotation, out _))
                    continue;

                Vector3 directionToTarget = target.position - bodyPosition;
                if (directionToTarget.sqrMagnitude <= Mathf.Epsilon)
                    continue;

                // Convert the complete world-space direction into Body-local
                // space before asking for a local post-animation rotation.
                Vector3 targetInBodySpace = Quaternion.Inverse(bodyWorldRotation) *
                    directionToTarget.normalized;
                Quaternion requestedLocalRotation = Quaternion.FromToRotation(BodyAimAxis, targetInBodySpace);
                Vector3 requestedEuler = requestedLocalRotation.eulerAngles;

                // This is the existing generic API: it applies one additive,
                // non-accumulating local rotation to this instance's Body slot.
                instance.Rotation.SetRuntimeLocalRotation(requestedEuler);
            }
        }

        private void CreateGrid()
        {
            instanceContainer = new GameObject("GPU Mech Instances").transform;
            float xOffset = (GridWidth - 1) * GridSpacing * 0.5f;
            float zOffset = (GridDepth - 1) * GridSpacing * 0.5f;

            // Follow Target's orientation while keeping all instance owners on
            // the existing ground plane. Target itself is neither moved nor
            // rotated. Its forward direction determines where the grid sits.
            Vector3 targetRight = Vector3.ProjectOnPlane(target.right, Vector3.up).normalized;
            Vector3 targetForward = Vector3.ProjectOnPlane(target.forward, Vector3.up).normalized;
            Vector3 gridCenter = target.position + target.forward * GridForwardOffset;
            gridCenter.y = 0f;

            for (int z = 0; z < GridDepth; z++)
            for (int x = 0; x < GridWidth; x++)
            {
                GameObject owner = new GameObject($"Mech Instance [{x}, {z}]");
                owner.SetActive(false);
                owner.transform.SetParent(instanceContainer);
                owner.transform.position = gridCenter + targetRight * (x * GridSpacing - xOffset) +
                    targetForward * (z * GridSpacing - zOffset);

                // The Transform supplies only a fixed position. Body is aimed on
                // the GPU; no character/root transform is rotated to face Target.
                GPUProceduralBoneRotation rotation = owner.AddComponent<GPUProceduralBoneRotation>();
                // Bone selection is serialized configuration on the generic
                // component, which intentionally has no public setter. Seed it
                // before activation; runtime aiming still uses only its API.
                JsonUtility.FromJsonOverwrite(BodyBoneConfiguration, rotation);
                owner.SetActive(true);

                if (!gpuInstanceManager.TryRegister(owner.transform, owner.transform, mechGpu, visualTransform,
                    out GPUInstanceBinding binding))
                {
                    Destroy(owner);
                    continue;
                }

                binding.TrySetAnimation(animationName, 1f, true);
                instances.Add(new DemoInstance { Rotation = rotation });
            }
        }

        private void OnDestroy()
        {
            if (gpuInstanceManager != null)
            {
                foreach (DemoInstance instance in instances)
                {
                    GPUProceduralBoneRotation rotation = instance.Rotation;
                    if (rotation == null) continue;
                    rotation.ClearRuntimeLocalRotation();
                    gpuInstanceManager.Unregister(rotation.transform);
                }
            }

            instances.Clear();

            if (instanceContainer != null)
                Destroy(instanceContainer.gameObject);
        }
    }
}
