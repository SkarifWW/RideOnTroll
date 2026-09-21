using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TrollBuildingMod
{
    public static class TrollBuildContext
    {
        public static Character TargetTroll = null;
        public static TrollPiecesContainer TargetContainer = null;
        public static Piece TargetPiece = null;
    }

    public static class TrollBuildConstants
    {
        public const string KeyTrollUUID = "TrollBuild_TrollUUID";
        public const string KeyParentPieceUID = "TrollBuild_ParentPieceUID";
        public const string KeyLocalPos = "TrollBuild_LocalPos";
        public const string KeyLocalRot = "TrollBuild_LocalRot";
        public const string KeyHasLocal = "TrollBuild_HasLocal";

        public static readonly int HashTrollUUID = KeyTrollUUID.GetStableHashCode();
        public static readonly int HashLocalPos = KeyLocalPos.GetStableHashCode();
        public static readonly int HashLocalRot = KeyLocalRot.GetStableHashCode();
        public static readonly int HashHasLocal = KeyHasLocal.GetStableHashCode();
    }

    public class TrollPieceTag : MonoBehaviour
    {
        public TrollPiecesContainer Container;
    }

    public class TrollPiecesContainer : MonoBehaviour
    {
        public Character TrollCharacter { get; private set; }
        public string TrollUUID = "";
        private readonly List<ZNetView> m_attachedPieces = new List<ZNetView>();
        public List<ZNetView> AttachedPieces => m_attachedPieces;

        public Transform PlatformAnchor { get; private set; }
        private Collider[] m_cachedTrollColliders = Array.Empty<Collider>();
        private bool m_isDestroyingPieces = false;

        // Отслеживание перемещения для переноса стоящего игрока
        private Vector3 m_prevAnchorPos;
        private Quaternion m_prevAnchorRot;

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

            if (TrollCharacter != null)
            {
                TrollCharacter.m_onDeath = (Action)Delegate.Combine(TrollCharacter.m_onDeath, new Action(OnTrollDeath));
            }
        }

        /// <summary>
        /// Якорь жестко привязан к корню тролля (Troll root).
        /// Движется движком Unity без скриптов, кадров задержки и рассинхронов.
        /// </summary>
        private void SetupPlatformAnchor()
        {
            if (PlatformAnchor != null) return;

            Transform existing = transform.Find("_TrollPlatformAnchor");
            if (existing != null)
            {
                PlatformAnchor = existing;
            }
            else
            {
                GameObject anchorObj = new GameObject("_TrollPlatformAnchor");
                anchorObj.transform.SetParent(transform, false);
                // Фиксированная позиция платформы на верхней части спины тролля
                anchorObj.transform.localPosition = new Vector3(0f, 3.35f, -0.25f);
                anchorObj.transform.localRotation = Quaternion.identity;
                anchorObj.transform.localScale = Vector3.one;
                PlatformAnchor = anchorObj.transform;
            }

            m_prevAnchorPos = PlatformAnchor.position;
            m_prevAnchorRot = PlatformAnchor.rotation;

            // Создаем страховочный коллайдер в основании платформы
            Transform catchFloor = PlatformAnchor.Find("_TrollCatchFloor");
            if (catchFloor == null)
            {
                GameObject catchObj = new GameObject("_TrollCatchFloor");
                catchObj.transform.SetParent(PlatformAnchor, false);
                catchObj.transform.localPosition = new Vector3(0f, -0.2f, 0f);
                catchObj.layer = LayerMask.NameToLayer("piece");

                BoxCollider box = catchObj.AddComponent<BoxCollider>();
                box.size = new Vector3(5f, 0.4f, 5f);

                TrollPieceTag tag = catchObj.AddComponent<TrollPieceTag>();
                tag.Container = this;
            }
        }

        public void CacheTrollColliders()
        {
            m_cachedTrollColliders = GetComponentsInChildren<Collider>(true);
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
                TrollRegistry.RegisterTroll(TrollUUID, this);
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
        /// Прикрепление детали: ставим в иерархию PlatformAnchor, выключаем лишние компоненты.
        /// НИКАКОЙ записи мировых координат в ZDO здесь нет и быть не должно!
        /// </summary>
        public void RegisterPiece(ZNetView pieceView, Vector3 localPos, Quaternion localRot)
        {
            if (pieceView == null) return;
            if (PlatformAnchor == null) SetupPlatformAnchor();

            if (!m_attachedPieces.Contains(pieceView))
            {
                m_attachedPieces.Add(pieceView);
            }

            // Переводим деталь в локальное пространство якоря тролля
            pieceView.transform.SetParent(PlatformAnchor, false);
            pieceView.transform.localPosition = localPos;
            pieceView.transform.localRotation = localRot;
            pieceView.transform.localScale = Vector3.one;

            TrollPieceTag tag = pieceView.GetComponent<TrollPieceTag>() ?? pieceView.gameObject.AddComponent<TrollPieceTag>();
            tag.Container = this;

            // Отключаем поддержку и статический кэш геометрии
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

            // Отключаем компонент сетевой синхронизации трансформов для этой детали
            ZSyncTransform syncTransform = pieceView.GetComponent<ZSyncTransform>();
            if (syncTransform != null) syncTransform.enabled = false;

            // Делаем коллайдеры выпуклыми для стабильности в движении
            foreach (var mc in pieceView.GetComponentsInChildren<MeshCollider>(true))
            {
                if (!mc.convex) mc.convex = true;
            }

            IgnoreCollisionsWithTroll(pieceView.gameObject);
        }

        private void IgnoreCollisionsWithTroll(GameObject pieceRoot)
        {
            Collider[] pieceCols = pieceRoot.GetComponentsInChildren<Collider>(true);
            foreach (var pc in pieceCols)
            {
                if (!pc) continue;
                foreach (var tc in m_cachedTrollColliders)
                {
                    if (tc && tc != pc) Physics.IgnoreCollision(tc, pc, true);
                }
            }
        }

        private void FixedUpdate()
        {
            if (PlatformAnchor == null) return;

            InitUUID();

            if (TrollPieceAttachmentQueue.HasPending(TrollUUID))
            {
                TrollPieceAttachmentQueue.CheckPending(TrollUUID, this);
            }

            // Перемещение стоящего игрока вместе с платформой тролля
            Player player = Player.m_localPlayer;
            if (player != null && !player.IsDead())
            {
                Collider groundCol = player.GetLastGroundCollider();
                if (groundCol != null)
                {
                    TrollPieceTag tag = groundCol.GetComponentInParent<TrollPieceTag>();
                    if (tag != null && tag.Container == this)
                    {
                        Vector3 curAnchorPos = PlatformAnchor.position;
                        Quaternion curAnchorRot = PlatformAnchor.rotation;

                        Vector3 deltaPos = curAnchorPos - m_prevAnchorPos;
                        Quaternion deltaRot = curAnchorRot * Quaternion.Inverse(m_prevAnchorRot);

                        Vector3 playerOffset = player.transform.position - m_prevAnchorPos;
                        Vector3 rotatedOffset = deltaRot * playerOffset;
                        Vector3 totalShift = (m_prevAnchorPos + rotatedOffset + deltaPos) - player.transform.position;

                        player.transform.position += totalShift;
                        Rigidbody playerRb = player.GetComponent<Rigidbody>();
                        if (playerRb != null) playerRb.position += totalShift;
                    }
                }
            }

            m_prevAnchorPos = PlatformAnchor.position;
            m_prevAnchorRot = PlatformAnchor.rotation;
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
            if (TrollCharacter != null && TrollCharacter.IsDead())
            {
                DestroyAllAttachedPieces();
            }
        }
    }

    public static class TrollRegistry
    {
        public static readonly Dictionary<string, TrollPiecesContainer> Containers = new Dictionary<string, TrollPiecesContainer>();

        public static void RegisterTroll(string uuid, TrollPiecesContainer container)
        {
            if (!string.IsNullOrEmpty(uuid) && container != null)
            {
                Containers[uuid] = container;
            }
        }

        public static void UnregisterTroll(string uuid)
        {
            if (!string.IsNullOrEmpty(uuid))
            {
                Containers.Remove(uuid);
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
            return !string.IsNullOrEmpty(trollUUID) && m_queue.TryGetValue(trollUUID, out var list) && list.Count > 0;
        }

        public static void ClearQueueForTroll(string trollUUID)
        {
            if (!string.IsNullOrEmpty(trollUUID)) m_queue.Remove(trollUUID);
        }

        public static void CheckPending(string trollUUID, TrollPiecesContainer container)
        {
            if (string.IsNullOrEmpty(trollUUID) || container == null) return;

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
                localPos = container.PlatformAnchor.InverseTransformPoint(pieceView.transform.position);
                localRot = Quaternion.Inverse(container.PlatformAnchor.rotation) * pieceView.transform.rotation;

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
        /// Попадание луча: фиксируем контекст тролля, но НЕ ломаем естественные нормали деталей.
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
                        troll = character;
                        container = troll.GetComponent<TrollPiecesContainer>() ?? troll.gameObject.AddComponent<TrollPiecesContainer>();
                        normal = container.PlatformAnchor.up;
                    }
                    else if (tag != null && tag.Container != null)
                    {
                        container = tag.Container;
                        troll = container.TrollCharacter;
                        hitPiece = hit.collider.GetComponentInParent<Piece>();
                        normal = hit.normal; // Родная нормаль грани детали
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

        /// <summary>
        /// Создание постройки: гарантированно берем ПОСЛЕДНЮЮ созданную деталь с конца m_placed.
        /// Вычисляем localPos/localRot и навсегда сохраняем в ZDO.
        /// </summary>
        [HarmonyPatch(typeof(Player), "PlacePiece")]
        [HarmonyPostfix]
        private static void PlacePiece_Postfix(Player __instance, Piece piece, Vector3 pos, Quaternion rot)
        {
            TrollPiecesContainer container = TrollBuildContext.TargetContainer;
            if (container == null || container.PlatformAnchor == null) return;

            container.InitUUID();
            Transform anchor = container.PlatformAnchor;

            Piece placedPiece = null;
            var placedList = AccessTools.StaticFieldRefAccess<List<IPlaced>>(typeof(Player), "m_placed");

            // Берем строго последний элемент списка (только что поставленный)
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

            TrollBuildContext.TargetTroll = null;
            TrollBuildContext.TargetContainer = null;
            TrollBuildContext.TargetPiece = null;
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

                if (TrollRegistry.Containers.TryGetValue(trollUUID, out TrollPiecesContainer container) && container != null)
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
    }
}