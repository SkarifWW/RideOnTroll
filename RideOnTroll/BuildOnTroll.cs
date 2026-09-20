using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace TrollBuildingMod
{
    public static class TrollBuildContext
    {
        public static Character TargetTroll = null;
        public static TrollPiecesContainer TargetContainer = null;
    }

    public static class TrollBuildConstants
    {
        public const string KeyTrollUUID = "TrollBuild_TrollUUID";
        public const string KeyParentUUID = "TrollBuild_ParentUUID";
        public const string KeyLocalPos = "TrollBuild_LocalPos";
        public const string KeyLocalRot = "TrollBuild_LocalRot";

        public static readonly int HashTrollUUID = KeyTrollUUID.GetStableHashCode();
        public static readonly int HashParentUUID = KeyParentUUID.GetStableHashCode();
        public static readonly int HashLocalPos = KeyLocalPos.GetStableHashCode();
        public static readonly int HashLocalRot = KeyLocalRot.GetStableHashCode();
    }

    public class TrollPieceTag : MonoBehaviour
    {
        public TrollPiecesContainer Container;
    }

    public class TrollBoneFollower : MonoBehaviour
    {
        public Transform TargetBone;
        public Vector3 LocalOffset = new Vector3(0f, 0.4f, -0.25f);

        private Vector3 m_previousPosition;
        private Quaternion m_previousRotation;

        private void Start()
        {
            if (TargetBone != null)
            {
                transform.position = TargetBone.TransformPoint(LocalOffset);
                transform.rotation = TargetBone.rotation;
            }
            m_previousPosition = transform.position;
            m_previousRotation = transform.rotation;
        }

        // Физическая синхронизация игрока с платформой в момент шага физики
        private void FixedUpdate()
        {
            if (TargetBone == null) return;

            Vector3 newPos = TargetBone.TransformPoint(LocalOffset);
            Quaternion newRot = TargetBone.rotation;

            Player player = Player.m_localPlayer;
            if (player != null && !player.IsDead())
            {
                Collider groundCol = player.GetLastGroundCollider();
                if (groundCol != null)
                {
                    TrollPieceTag tag = groundCol.GetComponentInParent<TrollPieceTag>();
                    if (tag != null && tag.Container != null && tag.Container.PlatformAnchor == transform)
                    {
                        // 1. Дельта линейного перемещения кости
                        Vector3 deltaPos = newPos - m_previousPosition;

                        // 2. Дельта вращения вокруг центра платформы (учет центробежного смещения)
                        Quaternion deltaRot = newRot * Quaternion.Inverse(m_previousRotation);
                        Vector3 playerOffset = player.transform.position - m_previousPosition;
                        Vector3 rotatedOffset = deltaRot * playerOffset;

                        Vector3 totalPlayerShift = (m_previousPosition + rotatedOffset + deltaPos) - player.transform.position;

                        // Перемещаем игрока синхронно с платформой
                        player.transform.position += totalPlayerShift;

                        Rigidbody playerRb = player.GetComponent<Rigidbody>();
                        if (playerRb != null)
                        {
                            playerRb.position += totalPlayerShift;
                        }
                    }
                }
            }

            transform.position = newPos;
            transform.rotation = newRot;
            transform.localScale = Vector3.one;

            m_previousPosition = newPos;
            m_previousRotation = newRot;

            // Принудительно актуализируем матрицы коллайдеров построек в PhysX
            Physics.SyncTransforms();
        }

        private void LateUpdate()
        {
            if (TargetBone == null) return;
            transform.position = TargetBone.TransformPoint(LocalOffset);
            transform.rotation = TargetBone.rotation;
        }
    }

    public class TrollPiecesContainer : MonoBehaviour
    {
        public Character TrollCharacter;
        public string TrollUUID = "";
        private readonly List<ZNetView> m_attachedPieces = new List<ZNetView>();
        public List<ZNetView> AttachedPieces => m_attachedPieces;

        public Transform PlatformAnchor { get; private set; }
        public TrollBoneFollower Follower { get; private set; }
        public readonly List<Transform> PlatformSnapPoints = new List<Transform>();
        private bool m_isDestroyingPieces = false;
        private Collider[] m_cachedTrollColliders = Array.Empty<Collider>();

        private Vector3 m_lastSyncedPos = Vector3.zero;
        private Quaternion m_lastSyncedRot = Quaternion.identity;
        private float m_syncTimer = 0f;

        private void Awake()
        {
            ZNetView nv = GetComponent<ZNetView>();
            if (nv == null || nv.GetZDO() == null)
            {
                enabled = false;
                return;
            }

            TrollCharacter = GetComponent<Character>();

            SetupPlatformAnchor();
            CacheTrollColliders();
            InitUUID();
            CreatePlatformSnapPoints();

            if (TrollCharacter != null)
            {
                TrollCharacter.m_onDeath = (Action)Delegate.Combine(TrollCharacter.m_onDeath, new Action(OnTrollDeath));
            }
        }

        private void SetupPlatformAnchor()
        {
            if (PlatformAnchor != null) return;

            Transform targetBone = FindSpineBone();

            GameObject anchorObj = new GameObject("_TrollPlatformRootAnchor");
            anchorObj.transform.SetParent(transform, false);
            anchorObj.transform.localScale = Vector3.one;

            Follower = anchorObj.AddComponent<TrollBoneFollower>();
            Follower.TargetBone = targetBone;

            PlatformAnchor = anchorObj.transform;
        }

        private Transform FindSpineBone()
        {
            string[] boneNames = { "Spine1", "Spine2", "Chest", "Spine", "body" };
            foreach (string bName in boneNames)
            {
                Transform found = Utils.FindChild(transform, bName);
                if (found != null) return found;
            }
            return transform;
        }

        public void CacheTrollColliders()
        {
            m_cachedTrollColliders = GetComponentsInChildren<Collider>(true);
        }

        public Collider[] GetCachedTrollColliders()
        {
            if (m_cachedTrollColliders == null || m_cachedTrollColliders.Length == 0)
            {
                CacheTrollColliders();
            }
            return m_cachedTrollColliders;
        }

        public void InitUUID()
        {
            if (!string.IsNullOrEmpty(TrollUUID)) return;

            ZNetView nview = GetComponent<ZNetView>();
            if (nview && nview.GetZDO() != null)
            {
                TrollUUID = nview.GetZDO().GetString(TrollBuildConstants.HashTrollUUID, "");
                if (string.IsNullOrEmpty(TrollUUID))
                {
                    TrollUUID = Guid.NewGuid().ToString();
                    nview.GetZDO().Set(TrollBuildConstants.HashTrollUUID, TrollUUID);
                }
                TrollRegistry.RegisterTroll(TrollUUID, TrollCharacter, this);
            }
        }

        private void Start()
        {
            InitUUID();
            SetupPlatformAnchor();
            CacheTrollColliders();

            if (!string.IsNullOrEmpty(TrollUUID))
            {
                TrollPieceAttachmentQueue.CheckPending(TrollUUID, this);
            }
        }

        public void CreatePlatformSnapPoints()
        {
            if (PlatformAnchor == null) SetupPlatformAnchor();

            // Создаем страховочный утолщенный пол-ловушку (Catch Floor) толщиной 1.5м
            Transform catchFloor = PlatformAnchor.Find("_TrollCatchFloor");
            if (catchFloor == null)
            {
                GameObject catchObj = new GameObject("_TrollCatchFloor");
                catchObj.transform.SetParent(PlatformAnchor, false);
                catchObj.transform.localPosition = new Vector3(0f, -0.75f, 0f);
                catchObj.layer = LayerMask.NameToLayer("piece");

                BoxCollider box = catchObj.AddComponent<BoxCollider>();
                box.size = new Vector3(5f, 1.5f, 5f);

                TrollPieceTag tag = catchObj.AddComponent<TrollPieceTag>();
                tag.Container = this;
            }

            Transform snapRoot = PlatformAnchor.Find("_TrollSnapGrid");
            if (snapRoot != null)
            {
                PlatformSnapPoints.Clear();
                foreach (Transform child in snapRoot)
                {
                    if (child.CompareTag("snappoint")) PlatformSnapPoints.Add(child);
                }
                return;
            }

            GameObject gridObj = new GameObject("_TrollSnapGrid");
            gridObj.transform.SetParent(PlatformAnchor, false);

            PlatformSnapPoints.Clear();
            for (float z = -1.5f; z <= 1.5f; z += 1.0f)
            {
                for (float x = -1.5f; x <= 1.5f; x += 1.0f)
                {
                    GameObject snap = new GameObject("snappoint");
                    snap.tag = "snappoint";
                    snap.transform.SetParent(gridObj.transform, false);
                    snap.transform.localPosition = new Vector3(x, 0f, z);
                    PlatformSnapPoints.Add(snap.transform);
                }
            }
        }

        public void RegisterPiece(ZNetView pieceView, Vector3 localPos, Quaternion localRot)
        {
            if (pieceView == null) return;
            if (PlatformAnchor == null) SetupPlatformAnchor();

            if (!m_attachedPieces.Contains(pieceView))
            {
                m_attachedPieces.Add(pieceView);
            }

            // Обеспечиваем выпуклость MeshCollider для точного просчета коллизий в динамике
            foreach (var mc in pieceView.GetComponentsInChildren<MeshCollider>(true))
            {
                if (!mc.convex)
                {
                    mc.convex = true;
                }
            }

            pieceView.transform.SetParent(PlatformAnchor);
            pieceView.transform.localPosition = localPos;
            pieceView.transform.localRotation = localRot;
            pieceView.transform.localScale = Vector3.one;

            TrollPieceTag tag = pieceView.GetComponent<TrollPieceTag>() ?? pieceView.gameObject.AddComponent<TrollPieceTag>();
            tag.Container = this;

            WearNTear wnt = pieceView.GetComponent<WearNTear>();
            if (wnt != null)
            {
                wnt.m_noSupportWear = true;
                wnt.m_staticPosition = false;
            }

            Piece p = pieceView.GetComponent<Piece>();
            if (p != null) p.m_noInWater = false;

            Floating floating = pieceView.GetComponent<Floating>();
            if (floating != null) floating.enabled = false;

            ZSyncTransform syncTransform = pieceView.GetComponent<ZSyncTransform>();
            if (syncTransform != null) syncTransform.enabled = false;

            FixMaterialShaders(pieceView);
            IgnoreCollisionsWithTroll(pieceView.gameObject);

            if (Player.m_localPlayer != null)
            {
                IgnoreCollisionsWithPlayer(Player.m_localPlayer.GetCollider());
            }

            if (pieceView.GetZDO() != null)
            {
                pieceView.GetZDO().SetPosition(pieceView.transform.position);
                pieceView.GetZDO().SetRotation(pieceView.transform.rotation);
            }
        }

        private static readonly int s_triplanarLocalPos = Shader.PropertyToID("_TriplanarLocalPos");
        private static void FixMaterialShaders(ZNetView nv)
        {
            foreach (Renderer renderer in nv.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || renderer.sharedMaterials == null) continue;
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat != null && mat.HasProperty(s_triplanarLocalPos))
                    {
                        mat.SetFloat(s_triplanarLocalPos, 1f);
                    }
                }
            }
        }

        private void IgnoreCollisionsWithTroll(GameObject pieceRoot)
        {
            Collider[] trollCols = GetCachedTrollColliders();
            Collider[] pieceCols = pieceRoot.GetComponentsInChildren<Collider>(true);

            foreach (var pc in pieceCols)
            {
                if (!pc) continue;
                foreach (var tc in trollCols)
                {
                    if (tc && tc != pc) Physics.IgnoreCollision(tc, pc, true);
                }
            }
        }

        public void IgnoreCollisionsWithPlayer(Collider playerCol)
        {
            if (!playerCol) return;
            Collider[] trollCols = GetCachedTrollColliders();
            foreach (var tc in trollCols)
            {
                if (tc && tc != playerCol) Physics.IgnoreCollision(tc, playerCol, true);
            }
        }

        public void DestroyAllAttachedPieces()
        {
            if (m_isDestroyingPieces) return;
            m_isDestroyingPieces = true;

            for (int i = m_attachedPieces.Count - 1; i >= 0; i--)
            {
                ZNetView pieceView = m_attachedPieces[i];
                if (pieceView == null || !pieceView.IsValid()) continue;

                pieceView.GetZDO().Set(TrollBuildConstants.HashParentUUID, "");

                Piece piece = pieceView.GetComponent<Piece>();
                if (piece != null) piece.DropResources(null);

                pieceView.Destroy();
            }

            m_attachedPieces.Clear();
            TrollPieceAttachmentQueue.ClearQueueForTroll(TrollUUID);
        }

        private void OnTrollDeath()
        {
            DestroyAllAttachedPieces();
        }

        private void OnDestroy()
        {
            TrollRegistry.UnregisterTroll(TrollUUID);

            if (ZNetScene.instance == null || ZNet.instance == null) return;

            if (TrollCharacter != null && TrollCharacter.IsDead())
            {
                DestroyAllAttachedPieces();
                return;
            }

            SyncZDOPositions();
        }

        private void FixedUpdate()
        {
            if (TrollCharacter == null) return;

            InitUUID();

            if (Player.m_localPlayer != null)
            {
                IgnoreCollisionsWithPlayer(Player.m_localPlayer.GetCollider());
            }

            if (TrollPieceAttachmentQueue.HasPending(TrollUUID))
            {
                TrollPieceAttachmentQueue.CheckPending(TrollUUID, this);
            }

            float moveDelta = Vector3.Distance(transform.position, m_lastSyncedPos);
            float rotDelta = Quaternion.Angle(transform.rotation, m_lastSyncedRot);

            m_syncTimer += Time.fixedDeltaTime;
            if (m_syncTimer >= 0.5f || moveDelta > 0.2f || rotDelta > 2f)
            {
                m_syncTimer = 0f;
                m_lastSyncedPos = transform.position;
                m_lastSyncedRot = transform.rotation;
                SyncZDOPositions();
            }
        }

        public void SyncZDOPositions()
        {
            ZNetView trollNv = GetComponent<ZNetView>();
            bool isOwner = trollNv != null && trollNv.IsValid() && trollNv.IsOwner();

            for (int i = m_attachedPieces.Count - 1; i >= 0; i--)
            {
                var piece = m_attachedPieces[i];
                if (piece == null || piece.GetZDO() == null)
                {
                    m_attachedPieces.RemoveAt(i);
                    continue;
                }

                if (isOwner && !piece.IsOwner())
                {
                    piece.ClaimOwnership();
                }

                if (piece.IsOwner())
                {
                    Vector3 curPos = piece.transform.position;
                    Quaternion curRot = piece.transform.rotation;

                    if ((piece.GetZDO().GetPosition() - curPos).sqrMagnitude > 0.001f)
                    {
                        piece.GetZDO().SetPosition(curPos);
                        piece.GetZDO().SetRotation(curRot);
                    }
                }
            }
        }
    }

    public static class TrollRegistry
    {
        public static readonly Dictionary<string, TrollPiecesContainer> ContainersByUUID = new Dictionary<string, TrollPiecesContainer>();

        public static void RegisterTroll(string uuid, Character troll, TrollPiecesContainer container)
        {
            if (string.IsNullOrEmpty(uuid) || container == null) return;
            ContainersByUUID[uuid] = container;

            ZNetView nv = troll.GetComponent<ZNetView>();
            if (nv != null && nv.GetZDO() != null)
            {
                string zdoidStr = nv.GetZDO().m_uid.ToString();
                if (!string.IsNullOrEmpty(zdoidStr) && zdoidStr != uuid)
                {
                    ContainersByUUID[zdoidStr] = container;
                }
            }
        }

        public static void UnregisterTroll(string uuid)
        {
            if (string.IsNullOrEmpty(uuid)) return;
            ContainersByUUID.Remove(uuid);
        }
    }

    public static class TrollPieceAttachmentQueue
    {
        private static readonly Dictionary<string, List<ZNetView>> m_queue = new Dictionary<string, List<ZNetView>>();

        public static void Enqueue(string trollUUID, ZNetView piece)
        {
            if (string.IsNullOrEmpty(trollUUID) || piece == null) return;

            if (!m_queue.ContainsKey(trollUUID))
                m_queue[trollUUID] = new List<ZNetView>();

            if (!m_queue[trollUUID].Contains(piece))
                m_queue[trollUUID].Add(piece);
        }

        public static bool HasPending(string trollUUID)
        {
            if (string.IsNullOrEmpty(trollUUID)) return false;
            return m_queue.TryGetValue(trollUUID, out var list) && list.Count > 0;
        }

        public static void ClearQueueForTroll(string trollUUID)
        {
            if (string.IsNullOrEmpty(trollUUID)) return;
            m_queue.Remove(trollUUID);
        }

        public static void CheckPending(string trollUUID, TrollPiecesContainer container)
        {
            if (string.IsNullOrEmpty(trollUUID) || !container) return;

            if (m_queue.TryGetValue(trollUUID, out var list))
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var pieceView = list[i];
                    if (pieceView && pieceView.IsValid())
                    {
                        AttachToTroll(container, pieceView);
                    }
                }
                m_queue.Remove(trollUUID);
            }
        }

        public static void AttachToTroll(TrollPiecesContainer container, ZNetView pieceView)
        {
            if (!container || !pieceView || !pieceView.IsValid()) return;

            container.InitUUID();
            Transform anchor = container.PlatformAnchor;

            Vector3 localPos = pieceView.GetZDO().GetVec3(TrollBuildConstants.HashLocalPos, Vector3.zero);
            Vector3 euler = pieceView.GetZDO().GetVec3(TrollBuildConstants.HashLocalRot, Vector3.zero);

            if (localPos == Vector3.zero && pieceView.transform.position != anchor.position)
            {
                localPos = anchor.InverseTransformPoint(pieceView.transform.position);
                euler = (Quaternion.Inverse(anchor.rotation) * pieceView.transform.rotation).eulerAngles;

                if (pieceView.IsOwner())
                {
                    pieceView.GetZDO().Set(TrollBuildConstants.HashLocalPos, localPos);
                    pieceView.GetZDO().Set(TrollBuildConstants.HashLocalRot, euler);
                }
            }

            container.RegisterPiece(pieceView, localPos, Quaternion.Euler(euler));
        }
    }

    [HarmonyPatch]
    public static class TrollBuildingPatches
    {
        private static readonly AccessTools.FieldRef<Player, GameObject> PlacementGhostRef =
            AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");

        private static readonly AccessTools.FieldRef<Player, int> PlaceRotationRef =
            AccessTools.FieldRefAccess<Player, int>("m_placeRotation");

        private static readonly AccessTools.FieldRef<Player, float> PlaceRotationDegreesRef =
            AccessTools.FieldRefAccess<Player, float>("m_placeRotationDegrees");

        private static readonly AccessTools.FieldRef<Player, Player.PlacementStatus> PlacementStatusRef =
            AccessTools.FieldRefAccess<Player, Player.PlacementStatus>("m_placementStatus");

        private struct GhostOriginalFlags
        {
            public bool GroundPiece;
            public bool GroundOnly;
            public bool CultivatedGroundOnly;
            public bool VegetationGroundOnly;
            public bool NotOnWood;
            public bool NotOnTiltingSurface;
            public bool NoInWater;
            public bool NoClipping;
            public bool Active;
        }
        private static GhostOriginalFlags s_originalGhostFlags;

        [HarmonyPatch(typeof(Player), "TestGhostClipping")]
        [HarmonyPrefix]
        private static bool TestGhostClipping_Prefix(ref bool __result)
        {
            if (TrollBuildContext.TargetTroll != null)
            {
                __result = false;
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(Piece), "GetSnapPoints", new[] { typeof(Vector3), typeof(float), typeof(List<Transform>), typeof(List<Piece>) })]
        [HarmonyPostfix]
        private static void Piece_GetSnapPoints_Postfix(Vector3 point, float radius, List<Transform> points, List<Piece> pieces)
        {
            TrollPiecesContainer container = TrollBuildContext.TargetContainer;
            if (container != null && container.PlatformSnapPoints.Count > 0)
            {
                points.AddRange(container.PlatformSnapPoints);
            }
        }

        public static Quaternion GetGhostRotation(float x, float y, float z)
        {
            Quaternion localEuler = Quaternion.Euler(x, y, z);
            if (TrollBuildContext.TargetContainer != null && TrollBuildContext.TargetContainer.PlatformAnchor != null)
            {
                return TrollBuildContext.TargetContainer.PlatformAnchor.rotation * localEuler;
            }
            return localEuler;
        }

        [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> UpdatePlacementGhost_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var eulerMethod = AccessTools.Method(typeof(Quaternion), nameof(Quaternion.Euler), new[] { typeof(float), typeof(float), typeof(float) });
            var customMethod = AccessTools.Method(typeof(TrollBuildingPatches), nameof(TrollBuildingPatches.GetGhostRotation));

            foreach (var instruction in instructions)
            {
                if (instruction.Calls(eulerMethod))
                {
                    yield return new CodeInstruction(OpCodes.Call, customMethod);
                }
                else
                {
                    yield return instruction;
                }
            }
        }

        [HarmonyPatch(typeof(Player), "PieceRayTest")]
        [HarmonyPrefix]
        private static bool PieceRayTest_Prefix(
            Player __instance,
            ref bool __result,
            out Vector3 point,
            out Vector3 normal,
            out Piece piece,
            out Heightmap heightmap,
            out Collider waterSurface,
            bool water)
        {
            point = Vector3.zero;
            normal = Vector3.up;
            piece = null;
            heightmap = null;
            waterSurface = null;
            TrollBuildContext.TargetTroll = null;
            TrollBuildContext.TargetContainer = null;

            if (GameCamera.instance == null)
            {
                __result = false;
                return false;
            }

            int layerMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid", "terrain", "character", "character_net", "vehicle");

            if (Physics.Raycast(GameCamera.instance.transform.position, GameCamera.instance.transform.forward, out RaycastHit hit, 50f, layerMask))
            {
                float maxDist = __instance.m_maxPlaceDistance;
                Transform eye = __instance.m_eye;

                if (eye != null && Vector3.Distance(eye.position, hit.point) < maxDist)
                {
                    Character character = hit.collider.GetComponentInParent<Character>();
                    Character troll = null;
                    TrollPiecesContainer container = null;

                    if (character != null && character.name.StartsWith("Troll", StringComparison.OrdinalIgnoreCase) && character.IsTamed())
                    {
                        troll = character;
                        container = troll.GetComponent<TrollPiecesContainer>() ?? troll.gameObject.AddComponent<TrollPiecesContainer>();
                    }
                    else
                    {
                        TrollPieceTag tag = hit.collider.GetComponentInParent<TrollPieceTag>();
                        if (tag != null && tag.Container != null)
                        {
                            container = tag.Container;
                            troll = container.TrollCharacter;
                        }
                    }

                    if (troll != null && container != null)
                    {
                        point = hit.point;
                        piece = hit.collider.GetComponentInParent<Piece>();
                        heightmap = null;
                        TrollBuildContext.TargetTroll = troll;
                        TrollBuildContext.TargetContainer = container;

                        normal = container.PlatformAnchor != null ? container.PlatformAnchor.up : Vector3.up;

                        __result = true;
                        return false;
                    }
                }
            }

            return true;
        }

        [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
        [HarmonyPrefix]
        private static void UpdatePlacementGhost_Prefix(Player __instance)
        {
            GameObject ghost = PlacementGhostRef != null ? PlacementGhostRef(__instance) : null;
            if (ghost == null) return;

            Piece piece = ghost.GetComponent<Piece>();
            if (piece == null) return;

            s_originalGhostFlags.Active = false;

            if (TrollBuildContext.TargetTroll != null)
            {
                s_originalGhostFlags.GroundPiece = piece.m_groundPiece;
                s_originalGhostFlags.GroundOnly = piece.m_groundOnly;
                s_originalGhostFlags.CultivatedGroundOnly = piece.m_cultivatedGroundOnly;
                s_originalGhostFlags.VegetationGroundOnly = piece.m_vegetationGroundOnly;
                s_originalGhostFlags.NotOnWood = piece.m_notOnWood;
                s_originalGhostFlags.NotOnTiltingSurface = piece.m_notOnTiltingSurface;
                s_originalGhostFlags.NoInWater = piece.m_noInWater;
                s_originalGhostFlags.NoClipping = piece.m_noClipping;
                s_originalGhostFlags.Active = true;

                piece.m_groundPiece = false;
                piece.m_groundOnly = false;
                piece.m_cultivatedGroundOnly = false;
                piece.m_vegetationGroundOnly = false;
                piece.m_notOnWood = false;
                piece.m_notOnTiltingSurface = false;
                piece.m_noInWater = false;
                piece.m_noClipping = false;
            }
        }

        [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
        [HarmonyPostfix]
        private static void UpdatePlacementGhost_Postfix(Player __instance)
        {
            GameObject ghost = PlacementGhostRef != null ? PlacementGhostRef(__instance) : null;

            if (ghost != null && s_originalGhostFlags.Active)
            {
                Piece piece = ghost.GetComponent<Piece>();
                if (piece != null)
                {
                    piece.m_groundPiece = s_originalGhostFlags.GroundPiece;
                    piece.m_groundOnly = s_originalGhostFlags.GroundOnly;
                    piece.m_cultivatedGroundOnly = s_originalGhostFlags.CultivatedGroundOnly;
                    piece.m_vegetationGroundOnly = s_originalGhostFlags.VegetationGroundOnly;
                    piece.m_notOnWood = s_originalGhostFlags.NotOnWood;
                    piece.m_notOnTiltingSurface = s_originalGhostFlags.NotOnTiltingSurface;
                    piece.m_noInWater = s_originalGhostFlags.NoInWater;
                    piece.m_noClipping = s_originalGhostFlags.NoClipping;
                }
                s_originalGhostFlags.Active = false;
            }

            Character troll = TrollBuildContext.TargetTroll;
            TrollPiecesContainer container = TrollBuildContext.TargetContainer;
            if (troll == null || container == null || ghost == null)
            {
                return;
            }

            if (PlacementStatusRef != null)
            {
                PlacementStatusRef(__instance) = Player.PlacementStatus.Valid;
                ghost.GetComponent<Piece>().SetInvalidPlacementHeightlight(false);
            }
        }

        [HarmonyPatch(typeof(Player), "PlacePiece")]
        [HarmonyPostfix]
        private static void PlacePiece_Postfix(Player __instance, Piece piece, Vector3 pos, Quaternion rot)
        {
            TrollPiecesContainer container = TrollBuildContext.TargetContainer;
            if (container == null) return;

            container.InitUUID();
            Transform anchor = container.PlatformAnchor;

            Piece placedPiece = null;
            var placedList = AccessTools.StaticFieldRefAccess<List<IPlaced>>(typeof(Player), "m_placed");
            if (placedList != null && placedList.Count > 0)
            {
                foreach (var item in placedList)
                {
                    if (item is Piece p && !Player.IsPlacementGhost(p.gameObject))
                    {
                        placedPiece = p;
                        break;
                    }
                }
            }

            if (placedPiece == null)
            {
                Collider[] colliders = Physics.OverlapSphere(pos, 2.5f);
                float minDist = 999f;
                foreach (var col in colliders)
                {
                    Piece p = col.GetComponentInParent<Piece>();
                    if (p != null && !Player.IsPlacementGhost(p.gameObject) && p.gameObject.name.StartsWith(piece.gameObject.name))
                    {
                        float dist = Vector3.Distance(p.transform.position, pos);
                        if (dist < minDist)
                        {
                            minDist = dist;
                            placedPiece = p;
                        }
                    }
                }
            }

            if (placedPiece != null)
            {
                ZNetView nview = placedPiece.GetComponent<ZNetView>();
                if (nview && nview.IsValid())
                {
                    Vector3 localPos = anchor.InverseTransformPoint(placedPiece.transform.position);
                    Quaternion localRot = Quaternion.Inverse(anchor.rotation) * placedPiece.transform.rotation;

                    string trollUUID = container.TrollUUID;
                    nview.GetZDO().Set(TrollBuildConstants.HashParentUUID, trollUUID);
                    nview.GetZDO().Set(TrollBuildConstants.HashLocalPos, localPos);
                    nview.GetZDO().Set(TrollBuildConstants.HashLocalRot, localRot.eulerAngles);

                    container.RegisterPiece(nview, localPos, localRot);
                }
            }

            TrollBuildContext.TargetTroll = null;
            TrollBuildContext.TargetContainer = null;
        }

        [HarmonyPatch(typeof(WearNTear), "UpdateSupport")]
        [HarmonyPrefix]
        private static bool WearNTear_UpdateSupport_Prefix(WearNTear __instance)
        {
            if (__instance.GetComponent<TrollPieceTag>() != null)
            {
                __instance.m_noSupportWear = true;
                return false;
            }

            ZNetView nv = __instance.GetComponent<ZNetView>();
            if (nv != null && nv.GetZDO() != null && !string.IsNullOrEmpty(nv.GetZDO().GetString(TrollBuildConstants.HashParentUUID, "")))
            {
                __instance.m_noSupportWear = true;
                return false;
            }

            return true;
        }

        [HarmonyPatch(typeof(ZNetView), "Awake")]
        [HarmonyPostfix]
        private static void ZNetView_Awake_Postfix(ZNetView __instance)
        {
            if (__instance.GetZDO() == null) return;

            string trollUUID = __instance.GetZDO().GetString(TrollBuildConstants.HashParentUUID, "");
            if (!string.IsNullOrEmpty(trollUUID))
            {
                if (__instance.GetComponent<TrollPieceTag>() != null) return;

                WearNTear wnt = __instance.GetComponent<WearNTear>();
                if (wnt != null)
                {
                    wnt.m_noSupportWear = true;
                    wnt.m_staticPosition = false;
                }

                if (TrollRegistry.ContainersByUUID.TryGetValue(trollUUID, out TrollPiecesContainer container) && container != null)
                {
                    TrollPieceAttachmentQueue.AttachToTroll(container, __instance);
                }
                else
                {
                    TrollPieceAttachmentQueue.Enqueue(trollUUID, __instance);
                }
            }
        }

        // Физический перенос игрока происходит в TrollBoneFollower.FixedUpdate.
        // Здесь мы просто возвращаем false, отменяя стандартное ванильное трение земли.
        [HarmonyPatch(typeof(Character), "ApplyGroundForce")]
        [HarmonyPrefix]
        private static bool ApplyGroundForce_Prefix(Character __instance, ref Vector3 vel, Vector3 targetVel)
        {
            if (__instance != Player.m_localPlayer) return true;

            Collider groundCollider = __instance.GetLastGroundCollider();
            if (groundCollider == null) return true;

            TrollPieceTag pieceTag = groundCollider.GetComponentInParent<TrollPieceTag>();
            if (pieceTag == null || pieceTag.Container == null) return true;

            return false;
        }
    }
}