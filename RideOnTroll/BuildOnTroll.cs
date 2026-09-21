using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace TrollBuildingMod
{
    public static class TrollBuildContext
    {
        public static Character TargetTroll = null;
        public static TrollPiecesContainer TargetContainer = null;
        public static Piece TargetPiece = null;          // Конкретная деталь, на которую смотрит игрок
        public static bool IsAimingAtTrollSkin = false;  // Строим на самом тролле или на существующей постройке
    }

    public static class TrollBuildConstants
    {
        public const string KeyTrollUUID = "TrollBuild_TrollUUID";
        public const string KeyParentPieceUID = "TrollBuild_ParentPieceUID";
        public const string KeyLocalPos = "TrollBuild_LocalPos";
        public const string KeyLocalRot = "TrollBuild_LocalRot";
        public const string KeyHasLocal = "TrollBuild_HasLocal";

        public static readonly int HashTrollUUID = KeyTrollUUID.GetStableHashCode();
        public static readonly int HashParentPieceUID = KeyParentPieceUID.GetStableHashCode();
        public static readonly int HashLocalPos = KeyLocalPos.GetStableHashCode();
        public static readonly int HashLocalRot = KeyLocalRot.GetStableHashCode();
        public static readonly int HashHasLocal = KeyHasLocal.GetStableHashCode();
    }

    public class TrollPieceTag : MonoBehaviour
    {
        public TrollPiecesContainer Container;
    }

    /// <summary>
    /// Якорь платформы на спине тролля.
    /// Позиция следует за спиной, но поворот ВСЕГДА строго выровнен по горизонту корпуса тролля.
    /// </summary>
    public class TrollBoneFollower : MonoBehaviour
    {
        public Character Troll;
        public Transform TargetBone;
        public Vector3 LocalOffset = new Vector3(0f, 0.45f, -0.2f);

        private Vector3 m_previousPosition;
        private Quaternion m_previousRotation;

        private void Start()
        {
            UpdateAnchorTransform();
            m_previousPosition = transform.position;
            m_previousRotation = transform.rotation;
        }

        public void UpdateAnchorTransform()
        {
            if (Troll == null) return;

            Vector3 worldPos = TargetBone != null
                ? TargetBone.TransformPoint(LocalOffset)
                : Troll.transform.position + Vector3.up * 3.5f;

            Vector3 trollForward = Vector3.ProjectOnPlane(Troll.transform.forward, Vector3.up).normalized;
            if (trollForward.sqrMagnitude < 0.001f) trollForward = Troll.transform.forward;
            Quaternion worldRot = Quaternion.LookRotation(trollForward, Vector3.up);

            transform.position = worldPos;
            transform.rotation = worldRot;
            transform.localScale = Vector3.one;
        }

        private void FixedUpdate()
        {
            if (Troll == null) return;

            Vector3 newPos = TargetBone != null
                ? TargetBone.TransformPoint(LocalOffset)
                : Troll.transform.position + Vector3.up * 3.5f;

            Vector3 trollForward = Vector3.ProjectOnPlane(Troll.transform.forward, Vector3.up).normalized;
            if (trollForward.sqrMagnitude < 0.001f) trollForward = Troll.transform.forward;
            Quaternion newRot = Quaternion.LookRotation(trollForward, Vector3.up);

            Player player = Player.m_localPlayer;
            if (player != null && !player.IsDead())
            {
                Collider groundCol = player.GetLastGroundCollider();
                if (groundCol != null)
                {
                    TrollPieceTag tag = groundCol.GetComponentInParent<TrollPieceTag>();
                    if (tag != null && tag.Container != null && tag.Container.PlatformAnchor == transform)
                    {
                        Vector3 deltaPos = newPos - m_previousPosition;
                        Quaternion deltaRot = newRot * Quaternion.Inverse(m_previousRotation);

                        Vector3 playerOffset = player.transform.position - m_previousPosition;
                        Vector3 rotatedOffset = deltaRot * playerOffset;
                        Vector3 totalPlayerShift = (m_previousPosition + rotatedOffset + deltaPos) - player.transform.position;

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

            m_previousPosition = newPos;
            m_previousRotation = newRot;

            Physics.SyncTransforms();
        }

        private void LateUpdate()
        {
            UpdateAnchorTransform();
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
        public readonly List<Transform> BaseSnapPoints = new List<Transform>();
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
            CreateBaseSnapPoints();

            if (TrollCharacter != null)
            {
                TrollCharacter.m_onDeath = (Action)Delegate.Combine(TrollCharacter.m_onDeath, new Action(OnTrollDeath));
            }
        }

        public void SetupPlatformAnchor()
        {
            if (PlatformAnchor != null) return;

            Transform targetBone = FindSpineBone();

            GameObject anchorObj = new GameObject("_TrollPlatformRootAnchor");
            anchorObj.transform.SetParent(transform, false);

            Follower = anchorObj.AddComponent<TrollBoneFollower>();
            Follower.Troll = TrollCharacter;
            Follower.TargetBone = targetBone;
            Follower.UpdateAnchorTransform();

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

        /// <summary>
        /// Стартовые точки привязки для закладки первого фундамента.
        /// </summary>
        public void CreateBaseSnapPoints()
        {
            if (PlatformAnchor == null) SetupPlatformAnchor();

            Transform catchFloor = PlatformAnchor.Find("_TrollCatchFloor");
            if (catchFloor == null)
            {
                GameObject catchObj = new GameObject("_TrollCatchFloor");
                catchObj.transform.SetParent(PlatformAnchor, false);
                catchObj.transform.localPosition = new Vector3(0f, -0.75f, 0f);
                catchObj.layer = LayerMask.NameToLayer("piece");

                BoxCollider box = catchObj.AddComponent<BoxCollider>();
                box.size = new Vector3(6f, 1.5f, 6f);

                TrollPieceTag tag = catchObj.AddComponent<TrollPieceTag>();
                tag.Container = this;
            }

            Transform snapRoot = PlatformAnchor.Find("_TrollBaseSnapGrid");
            if (snapRoot != null)
            {
                BaseSnapPoints.Clear();
                foreach (Transform child in snapRoot)
                {
                    if (child.CompareTag("snappoint")) BaseSnapPoints.Add(child);
                }
                return;
            }

            GameObject gridObj = new GameObject("_TrollBaseSnapGrid");
            gridObj.transform.SetParent(PlatformAnchor, false);

            BaseSnapPoints.Clear();
            for (float z = -1.5f; z <= 1.5f; z += 1.0f)
            {
                for (float x = -1.5f; x <= 1.5f; x += 1.0f)
                {
                    GameObject snap = new GameObject("snappoint");
                    snap.tag = "snappoint";
                    snap.transform.SetParent(gridObj.transform, false);
                    snap.transform.localPosition = new Vector3(x, 0f, z);
                    BaseSnapPoints.Add(snap.transform);
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

            foreach (var mc in pieceView.GetComponentsInChildren<MeshCollider>(true))
            {
                if (!mc.convex)
                {
                    mc.convex = true;
                }
            }

            // Физически прикрепляем деталь к платформе
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
            foreach (Renderer renderer in nv.GetComponentsInChildren<Renderer>(false))
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

                pieceView.GetZDO().Set(TrollBuildConstants.HashTrollUUID, "");

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

            // Удаляем как по UUID, так и все ссылки на этот контейнер
            if (ContainersByUUID.TryGetValue(uuid, out TrollPiecesContainer container))
            {
                List<string> keysToRemove = new List<string>();
                foreach (var pair in ContainersByUUID)
                {
                    if (pair.Value == container) keysToRemove.Add(pair.Key);
                }
                foreach (var k in keysToRemove) ContainersByUUID.Remove(k);
            }
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

            ZDO zdo = pieceView.GetZDO();
            bool hasSavedLocal = zdo.GetBool(TrollBuildConstants.HashHasLocal, false);

            Vector3 localPos;
            Quaternion localRot;

            if (hasSavedLocal)
            {
                localPos = zdo.GetVec3(TrollBuildConstants.HashLocalPos, Vector3.zero);
                localRot = Quaternion.Euler(zdo.GetVec3(TrollBuildConstants.HashLocalRot, Vector3.zero));
            }
            else
            {
                // Резервный расчет, если постройка пришла без флага
                localPos = anchor.InverseTransformPoint(pieceView.transform.position);
                localRot = Quaternion.Inverse(anchor.rotation) * pieceView.transform.rotation;

                if (pieceView.IsOwner())
                {
                    zdo.Set(TrollBuildConstants.HashHasLocal, true);
                    zdo.Set(TrollBuildConstants.HashLocalPos, localPos);
                    zdo.Set(TrollBuildConstants.HashLocalRot, localRot.eulerAngles);
                }
            }

            container.RegisterPiece(pieceView, localPos, localRot);
        }
    }

    [HarmonyPatch]
    public static class TrollBuildingPatches
    {
        private static readonly AccessTools.FieldRef<Player, GameObject> PlacementGhostRef =
            AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");

        private static readonly AccessTools.FieldRef<Character, Vector3> MoveDirRef =
            AccessTools.FieldRefAccess<Character, Vector3>("m_moveDir");

        private static readonly AccessTools.FieldRef<Player, int> ManualSnapPointRef =
            AccessTools.FieldRefAccess<Player, int>("m_manualSnapPoint");

        private static readonly AccessTools.FieldRef<Player, int> PlaceRotationRef =
            AccessTools.FieldRefAccess<Player, int>("m_placeRotation");

        private static readonly AccessTools.FieldRef<Player, float> PlaceRotationDegreesRef =
            AccessTools.FieldRefAccess<Player, float>("m_placeRotationDegrees");

        private static readonly AccessTools.FieldRef<Player, Player.PlacementStatus> PlacementStatusRef =
            AccessTools.FieldRefAccess<Player, Player.PlacementStatus>("m_placementStatus");

        private static readonly AccessTools.FieldRef<Player, RaycastHit[]> RaycastHoverHitsRef =
            AccessTools.FieldRefAccess<Player, RaycastHit[]>("m_raycastHoverHits");

        private static Vector3 s_lockedLocalPos = Vector3.zero;
        private static Quaternion s_lockedLocalRot = Quaternion.identity;
        private static TrollPiecesContainer s_lastContainer = null;
        private static string s_lastGhostPrefabName = "";
        private static int s_lastPlaceRot = -1;
        private static int s_lastSnapIndex = -2;
        private static bool s_hasLockedPosition = false;

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

        /// <summary>
        /// Добавляем точки привязки тролля ТОЛЬКО если игрок целится в самого тролля (закладка фундамента).
        /// Обычные постройки используют свои стандартные снап-поинты Valheim.
        /// </summary>
        [HarmonyPatch(typeof(Piece), "GetSnapPoints", new[] { typeof(Vector3), typeof(float), typeof(List<Transform>), typeof(List<Piece>) })]
        [HarmonyPostfix]
        private static void Piece_GetSnapPoints_Postfix(Vector3 point, float radius, List<Transform> points, List<Piece> pieces)
        {
            TrollPiecesContainer container = TrollBuildContext.TargetContainer;
            if (container != null && TrollBuildContext.IsAimingAtTrollSkin)
            {
                if (container.BaseSnapPoints.Count > 0)
                {
                    points.AddRange(container.BaseSnapPoints);
                }
            }
        }

        /// <summary>
        /// Корректный луч: различаем попадание в тело тролля и попадание в деталь на тролле.
        /// Сохраняем реальную целевую деталь (TargetPiece) и нормаль поверхности!
        /// </summary>
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
            TrollBuildContext.TargetPiece = null;
            TrollBuildContext.IsAimingAtTrollSkin = false;

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
                    TrollPieceTag tag = hit.collider.GetComponentInParent<TrollPieceTag>();

                    Character troll = null;
                    TrollPiecesContainer container = null;
                    Piece hitPiece = null;

                    if (character != null && character.name.StartsWith("Troll", StringComparison.OrdinalIgnoreCase) && character.IsTamed())
                    {
                        // Целимся в самого тролля
                        troll = character;
                        container = troll.GetComponent<TrollPiecesContainer>() ?? troll.gameObject.AddComponent<TrollPiecesContainer>();
                        TrollBuildContext.IsAimingAtTrollSkin = true;
                        normal = container.PlatformAnchor != null ? container.PlatformAnchor.up : Vector3.up;
                    }
                    else if (tag != null && tag.Container != null)
                    {
                        // Целимся в постройку, уже стоящую на тролле
                        container = tag.Container;
                        troll = container.TrollCharacter;
                        hitPiece = hit.collider.GetComponentInParent<Piece>();
                        TrollBuildContext.IsAimingAtTrollSkin = false;
                        normal = hit.normal; // Берем естественную нормаль грани (стена, пол, потолок)
                    }

                    if (troll != null && container != null)
                    {
                        point = hit.point;
                        piece = hitPiece;
                        heightmap = null;

                        TrollBuildContext.TargetTroll = troll;
                        TrollBuildContext.TargetContainer = container;
                        TrollBuildContext.TargetPiece = hitPiece;

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

            TrollPiecesContainer container = TrollBuildContext.TargetContainer;
            if (container == null || ghost == null || container.PlatformAnchor == null)
            {
                s_hasLockedPosition = false;
                s_lastContainer = null;
                return;
            }

            Transform anchor = container.PlatformAnchor;

            int placeRotation = PlaceRotationRef != null ? PlaceRotationRef(__instance) : 0;
            float rotationDegrees = PlaceRotationDegreesRef != null ? PlaceRotationDegreesRef(__instance) : 22.5f;

            Vector2 mouseDelta = ZInput.GetMouseDelta();
            bool hasMouseInput = mouseDelta.sqrMagnitude > 0.0001f;
            bool hasJoyInput = Mathf.Abs(ZInput.GetJoyRightStickX()) > 0.05f || Mathf.Abs(ZInput.GetJoyRightStickY()) > 0.05f;
            bool hasMoveInput = MoveDirRef != null && MoveDirRef(__instance).sqrMagnitude > 0.001f;
            bool rotChanged = (placeRotation != s_lastPlaceRot);

            int currentSnapIndex = ManualSnapPointRef != null ? ManualSnapPointRef(__instance) : -1;
            bool snapChanged = (currentSnapIndex != s_lastSnapIndex);
            bool contextChanged = (s_lastContainer != container || s_lastGhostPrefabName != ghost.name);

            bool playerActivelyAiming = hasMouseInput || hasJoyInput || hasMoveInput || rotChanged || snapChanged || contextChanged || !s_hasLockedPosition;

            // Если строитель свободно целится на спину тролля без снапа
            if (TrollBuildContext.IsAimingAtTrollSkin && playerActivelyAiming)
            {
                Quaternion localRot = Quaternion.Euler(0f, rotationDegrees * placeRotation, 0f);
                s_lockedLocalPos = anchor.InverseTransformPoint(ghost.transform.position);
                s_lockedLocalRot = localRot;

                s_lastPlaceRot = placeRotation;
                s_lastSnapIndex = currentSnapIndex;
                s_lastContainer = container;
                s_lastGhostPrefabName = ghost.name;
                s_hasLockedPosition = true;

                ghost.transform.position = anchor.TransformPoint(s_lockedLocalPos);
                ghost.transform.rotation = anchor.rotation * s_lockedLocalRot;
            }
            else if (!TrollBuildContext.IsAimingAtTrollSkin)
            {
                // При снапе к другой детали ПОЛНОСТЬЮ доверяем ванильному расчету снапа Valheim!
                s_hasLockedPosition = false;
            }

            if (PlacementStatusRef != null)
            {
                PlacementStatusRef(__instance) = Player.PlacementStatus.Valid;
                ghost.GetComponent<Piece>().SetInvalidPlacementHeightlight(false);
            }
        }

        /// <summary>
        /// ИСПРАВЛЕНО: Гарантированно берем ТОЛЬКО ЧТО созданную деталь с конца списка m_placed.
        /// Сохраняем точный локальный расчет и ParentPieceUID в ZDO.
        /// </summary>
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

            // КРИТИЧЕСКИЙ ФИКС: итерируем с конца, чтобы взять именно текущую постройку!
            if (placedList != null && placedList.Count > 0)
            {
                for (int i = placedList.Count - 1; i >= 0; i--)
                {
                    if (placedList[i] is Piece p && !Player.IsPlacementGhost(p.gameObject))
                    {
                        placedPiece = p;
                        break;
                    }
                }
            }

            // Запасной поиск в радиусе 1.5м с проверкой совпадения префаба
            if (placedPiece == null)
            {
                Collider[] colliders = Physics.OverlapSphere(pos, 1.5f);
                float minDist = 999f;
                foreach (var col in colliders)
                {
                    Piece p = col.GetComponentInParent<Piece>();
                    if (p != null && !Player.IsPlacementGhost(p.gameObject) && p.name.StartsWith(piece.name))
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

                    ZDO zdo = nview.GetZDO();
                    zdo.Set(TrollBuildConstants.HashTrollUUID, container.TrollUUID);
                    zdo.Set(TrollBuildConstants.HashLocalPos, localPos);
                    zdo.Set(TrollBuildConstants.HashLocalRot, localRot.eulerAngles);
                    zdo.Set(TrollBuildConstants.HashHasLocal, true);

                    // Сохраняем связь Piece -> ParentPiece
                    if (TrollBuildContext.TargetPiece != null)
                    {
                        ZNetView parentNv = TrollBuildContext.TargetPiece.GetComponent<ZNetView>();
                        if (parentNv != null && parentNv.GetZDO() != null)
                        {
                            zdo.Set(TrollBuildConstants.KeyParentPieceUID, parentNv.GetZDO().m_uid);
                        }
                    }

                    container.RegisterPiece(nview, localPos, localRot);
                }
            }

            s_hasLockedPosition = false;
            TrollBuildContext.TargetTroll = null;
            TrollBuildContext.TargetContainer = null;
            TrollBuildContext.TargetPiece = null;
            TrollBuildContext.IsAimingAtTrollSkin = false;
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
            if (nv != null && nv.GetZDO() != null && !string.IsNullOrEmpty(nv.GetZDO().GetString(TrollBuildConstants.HashTrollUUID, "")))
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

            string trollUUID = __instance.GetZDO().GetString(TrollBuildConstants.HashTrollUUID, "");
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

        [HarmonyPatch(typeof(Player), "FindHoverObject")]
        [HarmonyPostfix]
        private static void FindHoverObject_Postfix(Player __instance, ref GameObject hover, ref Character hoverCreature)
        {
            if (hover != null && hover.GetComponentInParent<Character>() is Character c && c.name.StartsWith("Troll", StringComparison.OrdinalIgnoreCase))
            {
                RaycastHit[] hits = RaycastHoverHitsRef != null ? RaycastHoverHitsRef(__instance) : null;
                if (hits == null) return;

                for (int i = 0; i < hits.Length; i++)
                {
                    Collider col = hits[i].collider;
                    if (col == null) break;

                    Hoverable h = col.GetComponentInParent<Hoverable>();
                    if (h != null && !(h is Character))
                    {
                        hover = (h as MonoBehaviour).gameObject;
                        return;
                    }

                    Interactable interactable = col.GetComponentInParent<Interactable>();
                    if (interactable != null && !(interactable is Character))
                    {
                        hover = (interactable as MonoBehaviour).gameObject;
                        return;
                    }
                }
            }
        }
    }

    [HarmonyPatch(typeof(ZSyncTransform))]
    public static class ZSyncTransform_TrollPiece_Patches
    {
        [HarmonyPatch("ClientSync")]
        [HarmonyPrefix]
        private static bool ClientSync_Prefix(ZSyncTransform __instance)
        {
            if (__instance == null) return true;

            if (__instance.GetComponent<TrollPieceTag>() != null)
            {
                return false;
            }

            if (__instance.transform.parent != null && __instance.transform.parent.GetComponentInParent<TrollPiecesContainer>() != null)
            {
                return false;
            }

            return true;
        }
    }
}