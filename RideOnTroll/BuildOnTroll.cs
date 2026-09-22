using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace TrollBuildingMod
{
    #region Константы и контекст

    public enum TrollBuildTargetType { None, TrollSkin, TrollPiece }

    public static class TrollBuildConstants
    {
        public const string KeyTrollUUID = "TrollBuild_TrollUUID";
        public const string KeyParentPieceUID = "TrollBuild_ParentPieceUID";
        public const string KeyLocalPos = "TrollBuild_LocalPos";
        public const string KeyLocalRotQ = "TrollBuild_LocalRotQ";   // новое: кватернион
        public const string KeyHasLocal = "TrollBuild_HasLocal";

        public static readonly int HashTrollUUID = KeyTrollUUID.GetStableHashCode();
        public static readonly int HashLocalPos = KeyLocalPos.GetStableHashCode();
        public static readonly int HashLocalRotQ = KeyLocalRotQ.GetStableHashCode();
        public static readonly int HashHasLocal = KeyHasLocal.GetStableHashCode();
        public static readonly KeyValuePair<int, int> ParentPieceHashPair = ZDO.GetHashZDOID(KeyParentPieceUID);

        // Точка "палубы" на спине (локальные координаты корня тролля)
        public static readonly Vector3 DefaultBuildRootOffset = new Vector3(0f, 3.35f, -0.25f);

        // Полуразмеры зоны строительства вокруг BuildRoot (метры, при масштабе тролля 1:1)
        public static readonly Vector3 BuildZoneHalfExtents = new Vector3(1.75f, 1.25f, 1.75f);

        // Прибавка к дальности прицеливания по троллю
        public const float TrollExtraReach = 4f;

        // Лимит скорости поворота тролля при наличии построек (град/с)
        public const float MaxTurnSpeedWithPieces = 60f;

        public static bool IsDedicatedServer => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
    }

    public static class TrollBuildContext
    {
        public static TrollBuildTargetType TargetType = TrollBuildTargetType.None;
        public static TrollPiecesContainer Container;
        public static Character TrollCharacter;
        public static Piece TargetPiece;

        public static bool IsActive => TargetType != TrollBuildTargetType.None && Container != null;
        public static Transform BuildRoot => Container != null ? Container.BuildRoot : null;

        public static void SetTargetSkin(TrollPiecesContainer c)
        {
            TargetType = TrollBuildTargetType.TrollSkin;
            Container = c;
            TrollCharacter = c != null ? c.TrollCharacter : null;
            TargetPiece = null;
        }

        public static void SetTargetPiece(TrollPiecesContainer c, Piece p)
        {
            TargetType = TrollBuildTargetType.TrollPiece;
            Container = c;
            TrollCharacter = c != null ? c.TrollCharacter : null;
            TargetPiece = p;
        }

        public static void Reset()
        {
            TargetType = TrollBuildTargetType.None;
            Container = null;
            TrollCharacter = null;
            TargetPiece = null;
        }
    }

    public class TrollPieceTag : MonoBehaviour
    {
        public TrollPiecesContainer Container;
    }

    #endregion

    #region MaterialFixer (порт из BuildOnShip)

    public static class TrollMaterialFixer
    {
        private static readonly int TriplanarLocalPos = Shader.PropertyToID("_TriplanarLocalPos");
        private static readonly int RippleDistance = Shader.PropertyToID("_RippleDistance");
        private static readonly int ValueNoise = Shader.PropertyToID("_ValueNoise");
        private static readonly Dictionary<Material, Material> s_cache = new Dictionary<Material, Material>();

        public static void FixPieceMaterials(ZNetView netView)
        {
            if (TrollBuildConstants.IsDedicatedServer || netView == null) return;

            foreach (Renderer r in netView.GetComponentsInChildren<Renderer>(false))
            {
                if (r == null) continue;
                if (r.GetComponentInParent<Switch>() != null) continue;
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;

                Material[] mats = r.sharedMaterials;
                if (mats == null || mats.Length == 0) continue;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    Material fixedMat = FixMaterial(mats[i]);
                    if (fixedMat != null && fixedMat != mats[i]) { mats[i] = fixedMat; changed = true; }
                }
                if (changed) r.sharedMaterials = mats;
            }
        }

        private static Material FixMaterial(Material original)
        {
            if (original == null) return null;
            if (!original.HasProperty(RippleDistance) &&
                !original.HasProperty(ValueNoise) &&
                !original.HasProperty(TriplanarLocalPos))
                return original;

            string shader = original.shader != null ? original.shader.name : "";
            if (shader.Contains("Vegetation") || shader.Contains("Rock") || shader.Contains("Terrain"))
                return original;

            if (s_cache.TryGetValue(original, out Material cached)) return cached;

            Material m = new Material(original);
            if (m.HasProperty(ValueNoise)) m.SetFloat(ValueNoise, 0f);
            if (m.HasProperty(RippleDistance)) m.SetFloat(RippleDistance, 0f);
            if (m.HasProperty(TriplanarLocalPos)) m.SetFloat(TriplanarLocalPos, 1f);
            s_cache[original] = m;
            return m;
        }
    }

    #endregion

    #region Контейнер тролля

    public class TrollPiecesContainer : MonoBehaviour
    {
        public Character TrollCharacter { get; private set; }
        public string TrollUUID { get; private set; } = "";

        private Transform m_buildRoot;
        public Transform BuildRoot { get { EnsureBuildRoot(); return m_buildRoot; } }

        private readonly List<ZNetView> m_attachedPieces = new List<ZNetView>();
        public List<ZNetView> AttachedPieces => m_attachedPieces;
        public int PieceCount => m_attachedPieces.Count;

        // Скорости платформы (мировые), для переноса игрока
        public Vector3 PlatformVelocity { get; private set; }
        public Vector3 PlatformAngularVelocity { get; private set; }
        private Vector3 m_prevPos;
        private Quaternion m_prevRot;
        private bool m_hasPrev;

        public readonly List<Transform> DeckSnapPoints = new List<Transform>();

        private Collider[] m_trollColliders = Array.Empty<Collider>();

        private Vector2s m_lastSector = new Vector2s(short.MinValue, short.MinValue);
        private Vector3 m_lastSyncedPos = Vector3.zero;
        private Quaternion m_lastSyncedRot = Quaternion.identity;
        private float m_syncTimer;
        private bool m_wasMoving;
        private bool m_isDestroyingPieces;
        private bool m_isRecheckingSupport;

        private void Awake()
        {
            TrollCharacter = GetComponent<Character>();
            m_prevPos = transform.position;
            m_prevRot = transform.rotation;
            CacheTrollColliders();
            if (TrollCharacter != null && TrollCharacter.m_onDeath != null || TrollCharacter != null)
            {
                try
                {
                    TrollCharacter.m_onDeath = (Action)Delegate.Combine(TrollCharacter.m_onDeath, new Action(OnTrollDeath));
                }
                catch { }
            }
        }

        private void Start()
        {
            InitUUID();
            EnsureBuildRoot();
            CacheTrollColliders();
            CreateDeckSnapPoints();
            m_lastSyncedPos = transform.position;
            m_lastSyncedRot = transform.rotation;
            if (ZoneSystem.instance != null) m_lastSector = ZoneSystem.GetZone(transform.position);

            if (!string.IsNullOrEmpty(TrollUUID))
                TrollPieceAttachmentQueue.CheckPending(TrollUUID, this);
        }

        public void InitUUID()
        {
            if (!string.IsNullOrEmpty(TrollUUID)) return;
            ZNetView nv = GetComponent<ZNetView>();
            if (nv != null && nv.GetZDO() != null)
            {
                TrollUUID = nv.GetZDO().m_uid.ToString();
                TrollRegistry.RegisterTroll(TrollUUID, this);
            }
        }

        public void EnsureBuildRoot()
        {
            if (m_buildRoot == null)
            {
                Transform existing = transform.Find("TrollBuildRoot");
                if (existing != null)
                {
                    m_buildRoot = existing;
                }
                else
                {
                    GameObject rootObj = new GameObject("TrollBuildRoot");
                    rootObj.transform.SetParent(transform, false);
                    rootObj.transform.localPosition = TrollBuildConstants.DefaultBuildRootOffset;
                    rootObj.transform.localRotation = Quaternion.identity;
                    m_buildRoot = rootObj.transform;
                }
            }

            // Компенсация масштаба тролля (world level / enemySpeedSize): lossyScale корня = 1,
            // чтобы детали не деформировались, а локальные координаты были в метрах.
            Vector3 s = transform.lossyScale;
            if (Mathf.Abs(s.x) > 0.001f && Mathf.Abs(s.y) > 0.001f && Mathf.Abs(s.z) > 0.001f)
            {
                Vector3 target = new Vector3(1f / s.x, 1f / s.y, 1f / s.z);
                if ((m_buildRoot.localScale - target).sqrMagnitude > 1e-6f)
                    m_buildRoot.localScale = target;
            }
        }

        public bool IsInBuildZone(Vector3 worldPoint)
        {
            Transform root = BuildRoot;
            if (root == null) return false;
            Vector3 lp = root.InverseTransformPoint(worldPoint);
            Vector3 he = TrollBuildConstants.BuildZoneHalfExtents;
            return Mathf.Abs(lp.x) <= he.x && Mathf.Abs(lp.y) <= he.y && Mathf.Abs(lp.z) <= he.z;
        }

        public void CreateDeckSnapPoints()
        {
            if (TrollBuildConstants.IsDedicatedServer) return;
            Transform root = BuildRoot;
            if (root == null) return;

            Transform snapRoot = root.Find("_DeckSnapGrid");
            if (snapRoot == null)
            {
                GameObject gridObj = new GameObject("_DeckSnapGrid");
                gridObj.transform.SetParent(root, false);
                snapRoot = gridObj.transform;

                float hx = Mathf.Max(0.5f, TrollBuildConstants.BuildZoneHalfExtents.x - 0.25f);
                float hz = Mathf.Max(0.5f, TrollBuildConstants.BuildZoneHalfExtents.z - 0.25f);
                for (float z = -hz; z <= hz + 0.01f; z += 1f)
                    for (float x = -hx; x <= hx + 0.01f; x += 1f)
                    {
                        GameObject snap = new GameObject("snappoint");
                        snap.tag = "snappoint";
                        snap.transform.SetParent(snapRoot, false);
                        snap.transform.localPosition = new Vector3(Mathf.Round(x * 2f) * 0.5f, 0f, Mathf.Round(z * 2f) * 0.5f);
                        DeckSnapPoints.Add(snap.transform);
                    }
            }
            else
            {
                DeckSnapPoints.Clear();
                foreach (Transform child in snapRoot)
                    if (child.CompareTag("snappoint")) DeckSnapPoints.Add(child);
            }
        }

        public void CacheTrollColliders()
        {
            m_trollColliders = GetComponentsInChildren<Collider>(true)
                .Where(c => c != null && c.GetComponentInParent<TrollPieceTag>() == null)
                .ToArray();
        }

        public void RegisterPiece(ZNetView pieceView, Vector3 localPos, Quaternion localRot)
        {
            if (pieceView == null) return;
            EnsureBuildRoot();

            if (!m_attachedPieces.Contains(pieceView)) m_attachedPieces.Add(pieceView);

            // 1. MeshCollider -> convex: compound-ригидбоди PhysX игнорирует невыпуклые меши
            foreach (MeshCollider mc in pieceView.GetComponentsInChildren<MeshCollider>(true))
                if (!mc.convex) mc.convex = true;

            // 2. Привязка
            pieceView.transform.SetParent(m_buildRoot, false);
            pieceView.transform.localPosition = localPos;
            pieceView.transform.localRotation = localRot;
            pieceView.transform.localScale = Vector3.one; // BuildRoot имеет lossyScale=1

            // 3. Тег
            TrollPieceTag tag = pieceView.GetComponent<TrollPieceTag>();
            if (tag == null) tag = pieceView.gameObject.AddComponent<TrollPieceTag>();
            tag.Container = this;

            // 4. Фиксация инерции тролля (иначе Unity пересчитает её из новых compound-коллайдеров)
            Rigidbody trollBody = GetComponent<Rigidbody>();
            if (trollBody != null)
            {
                trollBody.centerOfMass = trollBody.centerOfMass;
                if (trollBody.inertiaTensor != Vector3.zero)
                {
                    trollBody.inertiaTensor = trollBody.inertiaTensor;
                    trollBody.inertiaTensorRotation = trollBody.inertiaTensorRotation;
                }
            }

            // 5. WearNTear
            WearNTear wnt = pieceView.GetComponent<WearNTear>();
            if (wnt != null)
            {
                wnt.m_staticPosition = false;
                wnt.m_noSupportWear = true;
            }

            // 6. Отключаем сетевую синхронизацию трансформа и терраформ
            ZSyncTransform sync = pieceView.GetComponent<ZSyncTransform>();
            if (sync != null) sync.enabled = false;
            foreach (TerrainModifier tm in pieceView.GetComponentsInChildren<TerrainModifier>(true)) { tm.enabled = false; Destroy(tm); }
            foreach (TerrainOp op in pieceView.GetComponentsInChildren<TerrainOp>(true)) { op.enabled = false; Destroy(op); }

            // 7. Игнор коллизий с телом тролля (коллизии с игроком/миром остаются)
            IgnoreCollisionsWithTrollBody(pieceView.gameObject);

            // 8. Материалы и освещение
            TrollMaterialFixer.FixPieceMaterials(pieceView);
            if (!TrollBuildConstants.IsDedicatedServer)
            {
                foreach (Renderer r in pieceView.GetComponentsInChildren<Renderer>(false))
                {
                    if (r.GetComponentInParent<Switch>() != null) continue;
                    r.probeAnchor = transform;
                    r.lightProbeUsage = LightProbeUsage.BlendProbes;
                }
            }

            // 9. ZDO — мировая позиция (стриминг зон / сохранение)
            if (pieceView.GetZDO() != null)
            {
                pieceView.GetZDO().SetPosition(pieceView.transform.position);
                pieceView.GetZDO().SetRotation(pieceView.transform.rotation);
            }

            // 10. Пересчёт поддержки каскадом
            RecheckAllPiecesSupport(false);
        }

        public void IgnoreCollisionsWithTrollBody(GameObject pieceObj)
        {
            if (m_trollColliders == null || m_trollColliders.Length == 0) CacheTrollColliders();
            Collider[] pieceCols = pieceObj.GetComponentsInChildren<Collider>(true);
            foreach (Collider pc in pieceCols)
            {
                if (pc == null || pc.isTrigger) continue;
                foreach (Collider tc in m_trollColliders)
                    if (tc != null && tc != pc) Physics.IgnoreCollision(tc, pc, true);
            }
        }

        private void FixedUpdate()
        {
            ZNetView nv = GetComponent<ZNetView>();
            if (nv == null || nv.GetZDO() == null) return;

            if (string.IsNullOrEmpty(TrollUUID)) InitUUID();
            EnsureBuildRoot();

            if (!string.IsNullOrEmpty(TrollUUID) && TrollPieceAttachmentQueue.HasPending(TrollUUID))
                TrollPieceAttachmentQueue.CheckPending(TrollUUID, this);

            // --- Скорости платформы (линейная + угловая, из дельт трансформа) ---
            float dt = Time.fixedDeltaTime;
            Vector3 pos = transform.position;
            Quaternion rot = transform.rotation;
            if (m_hasPrev && dt > 0f)
            {
                Vector3 dp = pos - m_prevPos;
                if (dp.sqrMagnitude < 100f) // < 10 м за шаг, иначе телепорт
                {
                    PlatformVelocity = dp / dt;
                    Quaternion dq = rot * Quaternion.Inverse(m_prevRot);
                    dq.ToAngleAxis(out float angDeg, out Vector3 axis);
                    if (angDeg > 180f) angDeg -= 360f;
                    if (axis.sqrMagnitude > 1e-10f && Mathf.Abs(angDeg) > 0.001f)
                        PlatformAngularVelocity = axis.normalized * (angDeg * Mathf.Deg2Rad / dt);
                    else
                        PlatformAngularVelocity = Vector3.zero;
                }
                else
                {
                    PlatformVelocity = Vector3.zero;
                    PlatformAngularVelocity = Vector3.zero;
                }
            }
            m_prevPos = pos;
            m_prevRot = rot;
            m_hasPrev = true;

            // --- Синк ZDO деталей (паттерн корабля: владелец тролля клэмит и пишет) ---
            float moveDelta = Vector3.Distance(pos, m_lastSyncedPos);
            float rotDelta = Quaternion.Angle(rot, m_lastSyncedRot);
            bool moving = moveDelta > 0.05f || rotDelta > 0.5f;

            if (moving)
            {
                m_wasMoving = true;
                if (ZoneSystem.instance != null)
                {
                    Vector2s sector = ZoneSystem.GetZone(pos);
                    if (sector != m_lastSector || moveDelta > 10f)
                    {
                        m_lastSector = sector;
                        m_lastSyncedPos = pos; m_lastSyncedRot = rot;
                        SyncPiecesWorldZDO();
                    }
                }
            }
            else if (m_wasMoving)
            {
                m_wasMoving = false;
                m_lastSyncedPos = pos; m_lastSyncedRot = rot;
                if (ZoneSystem.instance != null) m_lastSector = ZoneSystem.GetZone(pos);
                SyncPiecesWorldZDO(); // финальная фиксация при остановке
            }

            m_syncTimer += dt;
            if (m_syncTimer >= 0.5f)
            {
                m_syncTimer = 0f;
                if (moving)
                {
                    m_lastSyncedPos = pos; m_lastSyncedRot = rot;
                    SyncPiecesWorldZDO();
                }
            }
        }

        public void SyncPiecesWorldZDO()
        {
            ZNetView trollNv = GetComponent<ZNetView>();
            bool isTrollOwner = trollNv != null && trollNv.IsValid() && trollNv.IsOwner();

            for (int i = m_attachedPieces.Count - 1; i >= 0; i--)
            {
                var piece = m_attachedPieces[i];
                if (piece == null || !piece.IsValid() || piece.GetZDO() == null)
                {
                    m_attachedPieces.RemoveAt(i);
                    continue;
                }

                if (isTrollOwner && !piece.IsOwner())
                    piece.ClaimOwnership();

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

        public void OnPieceDestroyed(WearNTear wnt)
        {
            if (wnt == null) return;
            ZNetView nv = wnt.GetComponent<ZNetView>();
            if (nv != null) m_attachedPieces.Remove(nv);
            m_attachedPieces.RemoveAll(p => p == null || !p.IsValid());
            RecheckAllPiecesSupport(true);
        }

        public void RecheckAllPiecesSupport(bool triggerDestruction = true)
        {
            if (m_attachedPieces.Count == 0 || m_isRecheckingSupport) return;
            m_isRecheckingSupport = true;
            try
            {
                m_attachedPieces.RemoveAll(p => p == null || !p.IsValid());
                var sorted = m_attachedPieces.ToList();
                sorted.Sort((a, b) => a.transform.localPosition.y.CompareTo(b.transform.localPosition.y));
                foreach (var v in sorted)
                {
                    WearNTear w = v != null ? v.GetComponent<WearNTear>() : null;
                    if (w != null) TrollBuildSupport.CalculateTrollPieceSupport(w);
                }
            }
            finally { m_isRecheckingSupport = false; }
        }

        public void OnTrollDeath()
        {
            if (m_isDestroyingPieces) return;
            m_isDestroyingPieces = true;

            for (int i = m_attachedPieces.Count - 1; i >= 0; i--)
            {
                ZNetView v = m_attachedPieces[i];
                if (v == null || !v.IsValid()) continue;
                v.GetZDO().Set(TrollBuildConstants.HashTrollUUID, "");
                Piece p = v.GetComponent<Piece>();
                if (p != null) p.DropResources(null);
                v.Destroy();
            }
            m_attachedPieces.Clear();
            TrollPieceAttachmentQueue.ClearQueueForTroll(TrollUUID);
        }

        private void OnDestroy()
        {
            TrollRegistry.UnregisterTroll(TrollUUID);
            if (ZNetScene.instance == null || ZNet.instance == null) return;

            if (TrollCharacter != null && TrollCharacter.IsDead())
            {
                OnTrollDeath();
                return;
            }

            // Выгрузка зоны: зафиксировать ZDO и отцепить детали от GO тролля
            SyncPiecesWorldZDO();
            for (int i = 0; i < m_attachedPieces.Count; i++)
            {
                var piece = m_attachedPieces[i];
                if (piece != null) piece.transform.SetParent(null);
            }
        }
    }

    #endregion

    #region Реестр и очередь привязки

    public static class TrollRegistry
    {
        private static readonly Dictionary<string, TrollPiecesContainer> s_trolls = new Dictionary<string, TrollPiecesContainer>();

        public static void RegisterTroll(string uuid, TrollPiecesContainer c)
        {
            if (!string.IsNullOrEmpty(uuid) && c != null) s_trolls[uuid] = c;
        }

        public static void UnregisterTroll(string uuid)
        {
            if (!string.IsNullOrEmpty(uuid)) s_trolls.Remove(uuid);
        }

        public static bool TryGetTroll(string uuid, out TrollPiecesContainer c)
        {
            c = null;
            if (string.IsNullOrEmpty(uuid)) return false;
            return s_trolls.TryGetValue(uuid, out c) && c != null;
        }
    }

    public static class TrollPieceAttachmentQueue
    {
        private static readonly Dictionary<string, List<ZNetView>> s_pending = new Dictionary<string, List<ZNetView>>();
        private static readonly Quaternion Sentinel = new Quaternion(-1f, 0f, 0f, 0f);

        public static void Enqueue(string uuid, ZNetView v)
        {
            if (string.IsNullOrEmpty(uuid) || v == null) return;
            if (!s_pending.TryGetValue(uuid, out var list)) { list = new List<ZNetView>(); s_pending[uuid] = list; }
            if (!list.Contains(v)) list.Add(v);
        }

        public static bool HasPending(string uuid)
        {
            return !string.IsNullOrEmpty(uuid) && s_pending.TryGetValue(uuid, out var list) && list.Count > 0;
        }

        public static void ClearQueueForTroll(string uuid)
        {
            if (!string.IsNullOrEmpty(uuid)) s_pending.Remove(uuid);
        }

        public static void CheckPending(string uuid, TrollPiecesContainer container)
        {
            if (string.IsNullOrEmpty(uuid) || container == null) return;
            if (!s_pending.TryGetValue(uuid, out var list)) return;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var v = list[i];
                if (v == null || !v.IsValid()) { list.RemoveAt(i); continue; }
                Attach(container, v);
                list.RemoveAt(i);
            }
            if (list.Count == 0) s_pending.Remove(uuid);
        }

        public static void Attach(TrollPiecesContainer container, ZNetView pieceView)
        {
            if (!container || !pieceView || !pieceView.IsValid()) return;
            container.EnsureBuildRoot();
            ZDO zdo = pieceView.GetZDO();
            if (zdo == null) return;

            Vector3 lp;
            Quaternion lr;
            bool hasLocal = zdo.GetBool(TrollBuildConstants.HashHasLocal, false);
            Quaternion q = hasLocal ? zdo.GetQuaternion(TrollBuildConstants.HashLocalRotQ, Sentinel) : Sentinel;

            if (q != Sentinel)
            {
                lp = zdo.GetVec3(TrollBuildConstants.HashLocalPos, Vector3.zero);
                lr = q;
            }
            else
            {
                Transform root = container.BuildRoot;
                lp = root.InverseTransformPoint(pieceView.transform.position);
                lr = Quaternion.Inverse(root.rotation) * pieceView.transform.rotation;
                if (pieceView.IsOwner())
                {
                    zdo.Set(TrollBuildConstants.HashHasLocal, true);
                    zdo.Set(TrollBuildConstants.HashLocalPos, lp);
                    zdo.Set(TrollBuildConstants.HashLocalRotQ, lr);
                }
            }
            container.RegisterPiece(pieceView, lp, lr);
        }
    }

    #endregion

    #region Поддержка (ваниль-формула)

    public static class TrollBuildSupport
    {
        private static readonly AccessTools.FieldRef<WearNTear, float> SupportField =
            AccessTools.FieldRefAccess<WearNTear, float>("m_support");

        private static readonly int StaticMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "terrain", "piece");
        private static readonly Collider[] s_overlap = new Collider[64];
        private static readonly List<Vector3> s_points = new List<Vector3>();
        private static readonly List<float> s_pointValues = new List<float>();

        public static void CalculateTrollPieceSupport(WearNTear wnt)
        {
            if (wnt == null) return;
            ZNetView nv = wnt.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid()) return;
            TrollPieceTag tag = wnt.GetComponent<TrollPieceTag>();
            if (tag == null || tag.Container == null) return;

            GetMaterialProperties(wnt.m_materialType, out float max, out float _, out float vLoss, out float hLoss);

            float support;
            ZDOID parentId = nv.GetZDO().GetZDOID(TrollBuildConstants.ParentPieceHashPair);
            if (parentId == ZDOID.None)
            {
                support = max; // фундамент на коже тролля
            }
            else
            {
                support = ComputeFromSiblings(wnt, tag.Container, max, vLoss, hLoss);
            }

            support = Mathf.Clamp(support, 0f, max);
            SupportField(wnt) = support; // владелец читает поле
            if (nv.IsOwner()) nv.GetZDO().Set(ZDOVars.s_support, support); // остальные читают ZDO
        }

        private static float ComputeFromSiblings(WearNTear wnt, TrollPiecesContainer container, float max, float vLoss, float hLoss)
        {
            Vector3 com = wnt.transform.position + wnt.transform.rotation * wnt.m_comOffset;
            float best = 0f;

            // Касание статичного мира/рельефа — полная опора (ванильная семантика)
            if (TouchesStaticWorld(wnt)) return max;

            s_points.Clear();
            s_pointValues.Clear();

            foreach (ZNetView siblingView in container.AttachedPieces)
            {
                if (siblingView == null || !siblingView.IsValid()) continue;
                WearNTear other = siblingView.GetComponent<WearNTear>();
                if (other == null || other == wnt || !other.m_supports) continue;

                float otherSupport = ReadSupport(other);
                if (otherSupport <= 0f) continue;

                Vector3 otherCom = other.transform.position + other.transform.rotation * other.m_comOffset;
                float dist = Vector3.Distance(com, otherCom) + 0.1f;
                float dist2 = Vector3.Distance(com, other.transform.position) + 0.1f;
                if (dist2 < dist && !other.m_forceCorrectCOMCalculation && !wnt.m_forceCorrectCOMCalculation) dist = dist2;

                best = Mathf.Max(best, otherSupport - vLoss * dist * otherSupport);

                Vector3 sp = FindSupportPoint(com, other);
                if (sp.y < com.y + 0.05f)
                {
                    Vector3 dir = sp - com;
                    if (dir.sqrMagnitude > 1e-6f)
                    {
                        dir.Normalize();
                        if (dir.y < 0f)
                        {
                            float t = Mathf.Acos(1f - Mathf.Abs(dir.y)) / 1.5707964f;
                            float loss = Mathf.Lerp(vLoss, hLoss, t);
                            best = Mathf.Max(best, otherSupport - loss * dist * otherSupport);
                        }
                    }
                    s_points.Add(sp);
                    s_pointValues.Add(otherSupport - hLoss * dist * otherSupport);
                }
            }

            // Правило двух опорных точек (угол >= 100°) — как в ванили
            for (int i = 0; i < s_points.Count - 1; i++)
            {
                Vector3 a = s_points[i] - com; a.y = 0f;
                for (int j = i + 1; j < s_points.Count; j++)
                {
                    float avg = (s_pointValues[i] + s_pointValues[j]) * 0.5f;
                    if (avg > best)
                    {
                        Vector3 b = s_points[j] - com; b.y = 0f;
                        if (a.sqrMagnitude > 1e-6f && b.sqrMagnitude > 1e-6f && Vector3.Angle(a, b) >= 100f)
                            best = avg;
                    }
                }
            }
            return best;
        }

        private static bool TouchesStaticWorld(WearNTear wnt)
        {
            Bounds bounds = default;
            bool has = false;
            foreach (Collider c in wnt.GetComponentsInChildren<Collider>(true))
            {
                if (c == null || c.isTrigger || !c.enabled) continue;
                if (!has) { bounds = c.bounds; has = true; }
                else bounds.Encapsulate(c.bounds);
            }
            if (!has) return false;
            bounds.Expand(0.3f);

            int n = Physics.OverlapBoxNonAlloc(bounds.center, bounds.extents, s_overlap, Quaternion.identity, StaticMask);
            for (int i = 0; i < n; i++)
            {
                Collider c = s_overlap[i];
                if (c == null || c.isTrigger) continue;
                if (c.GetComponentInParent<TrollPieceTag>() != null) continue;        // детали тролля
                if (c.GetComponentInParent<TrollPiecesContainer>() != null) continue; // тело тролля
                WearNTear other = c.GetComponentInParent<WearNTear>();
                if (other == wnt) continue;                                            // свои коллайдеры
                if (other != null && c.attachedRigidbody != null) continue;            // чужие подвижные объекты
                return true; // terrain / статика / обычная мировая деталь
            }
            return false;
        }

        private static Vector3 FindSupportPoint(Vector3 com, WearNTear other)
        {
            Vector3 best = other.transform.position;
            float bestDist = float.MaxValue;
            foreach (Collider c in other.GetComponentsInChildren<Collider>(true))
            {
                if (c == null || c.isTrigger || !c.enabled) continue;
                MeshCollider mc = c as MeshCollider;
                if (mc != null && !mc.convex) continue;
                Vector3 p = c.ClosestPoint(com);
                float d = (p - com).sqrMagnitude;
                if (d < bestDist) { bestDist = d; best = p; }
            }
            return best;
        }

        private static float ReadSupport(WearNTear other)
        {
            ZNetView nv = other.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid()) return 0f;
            if (nv.IsOwner()) return SupportField(other);
            GetMaterialProperties(other.m_materialType, out float max, out _, out _, out _);
            return nv.GetZDO().GetFloat(ZDOVars.s_support, max);
        }

        // Точная таблица из WearNTear.GetMaterialProperties
        public static void GetMaterialProperties(WearNTear.MaterialType type,
            out float maxSupport, out float minSupport, out float verticalLoss, out float horizontalLoss)
        {
            switch (type)
            {
                case WearNTear.MaterialType.Wood: maxSupport = 100f; minSupport = 10f; verticalLoss = 0.125f; horizontalLoss = 0.2f; return;
                case WearNTear.MaterialType.Stone: maxSupport = 1000f; minSupport = 100f; verticalLoss = 0.125f; horizontalLoss = 1f; return;
                case WearNTear.MaterialType.Iron: maxSupport = 1500f; minSupport = 20f; verticalLoss = 0.07692308f; horizontalLoss = 0.07692308f; return;
                case WearNTear.MaterialType.HardWood: maxSupport = 140f; minSupport = 10f; verticalLoss = 0.1f; horizontalLoss = 0.16666667f; return;
                case WearNTear.MaterialType.Marble: maxSupport = 1500f; minSupport = 100f; verticalLoss = 0.125f; horizontalLoss = 0.5f; return;
                case WearNTear.MaterialType.Ashstone: maxSupport = 2000f; minSupport = 100f; verticalLoss = 0.1f; horizontalLoss = 0.33333334f; return;
                case WearNTear.MaterialType.Ancient: maxSupport = 5000f; minSupport = 100f; verticalLoss = 0.06666667f; horizontalLoss = 0.25f; return;
                case WearNTear.MaterialType.Ice: maxSupport = 1000f; minSupport = 100f; verticalLoss = 0.125f; horizontalLoss = 0.33333334f; return;
                case WearNTear.MaterialType.Timberwood: maxSupport = 200f; minSupport = 10f; verticalLoss = 0.07692308f; horizontalLoss = 0.2f; return;
                default: maxSupport = 0f; minSupport = 0f; verticalLoss = 0f; horizontalLoss = 0f; return;
            }
        }
    }

    #endregion

    #region Harmony-патчи

    [HarmonyPatch]
    public static class TrollBuildingPatches
    {
        private static readonly AccessTools.FieldRef<Player, GameObject> PlacementGhostRef =
            AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");
        private static readonly AccessTools.FieldRef<Player, int> PlaceRayMaskRef =
            AccessTools.FieldRefAccess<Player, int>("m_placeRayMask");
        private static readonly AccessTools.FieldRef<Player, int> PlaceWaterRayMaskRef =
            AccessTools.FieldRefAccess<Player, int>("m_placeWaterRayMask");
        private static readonly AccessTools.FieldRef<Player, int> PlaceRotationRef =
            AccessTools.FieldRefAccess<Player, int>("m_placeRotation");
        private static readonly AccessTools.FieldRef<Player, float> PlaceRotationDegreesRef =
            AccessTools.FieldRefAccess<Player, float>("m_placeRotationDegrees");
        private static readonly AccessTools.FieldRef<Player, Player.PlacementStatus> PlacementStatusRef =
            AccessTools.FieldRefAccess<Player, Player.PlacementStatus>("m_placementStatus");
        private static readonly AccessTools.FieldRef<Player, int> ManualSnapPointRef =
            AccessTools.FieldRefAccess<Player, int>("m_manualSnapPoint");
        private static readonly AccessTools.FieldRef<Character, Vector3> MoveDirRef =
            AccessTools.FieldRefAccess<Character, Vector3>("m_moveDir");
        private static readonly AccessTools.FieldRef<Character, Rigidbody> LastGroundBodyRef =
            AccessTools.FieldRefAccess<Character, Rigidbody>("m_lastGroundBody");
        private static readonly AccessTools.FieldRef<Character, Collider> LastGroundColliderRef =
            AccessTools.FieldRefAccess<Character, Collider>("m_lastGroundCollider");
        private static readonly AccessTools.FieldRef<Character, Rigidbody> LastAttachBodyRef =
            AccessTools.FieldRefAccess<Character, Rigidbody>("m_lastAttachBody");
        private static readonly AccessTools.FieldRef<Character, Vector3> LastAttachPosRef =
            AccessTools.FieldRefAccess<Character, Vector3>("m_lastAttachPos");
        private static readonly AccessTools.FieldRef<Character, Rigidbody> BodyRef =
            AccessTools.FieldRefAccess<Character, Rigidbody>("m_body");

        private static bool IsTroll(Character c) =>
            c != null && c.name.StartsWith("Troll", StringComparison.OrdinalIgnoreCase);

        // ============ Гашение флагов ghost (в том же кадре, что и валидация) ============

        private struct GhostFlags
        {
            public bool GroundPiece, GroundOnly, CultivatedGroundOnly, VegetationGroundOnly;
            public bool NotOnWood, NotOnTiltingSurface, NoInWater, NoClipping;
        }
        private static GhostFlags s_savedFlags;
        private static bool s_flagsActive;
        private static Piece s_flagsPiece;

        private static void ClearGhostPlacementFlags(Player player)
        {
            GameObject ghost = PlacementGhostRef(player);
            if (ghost == null) return;
            Piece p = ghost.GetComponent<Piece>();
            if (p == null) return;
            if (s_flagsActive) RestoreGhostPlacementFlags(player);

            s_savedFlags = new GhostFlags
            {
                GroundPiece = p.m_groundPiece,
                GroundOnly = p.m_groundOnly,
                CultivatedGroundOnly = p.m_cultivatedGroundOnly,
                VegetationGroundOnly = p.m_vegetationGroundOnly,
                NotOnWood = p.m_notOnWood,
                NotOnTiltingSurface = p.m_notOnTiltingSurface,
                NoInWater = p.m_noInWater,
                NoClipping = p.m_noClipping
            };
            s_flagsActive = true;
            s_flagsPiece = p;

            p.m_groundPiece = false;
            p.m_groundOnly = false;
            p.m_cultivatedGroundOnly = false;
            p.m_vegetationGroundOnly = false;
            p.m_notOnWood = false;
            p.m_notOnTiltingSurface = false;
            p.m_noInWater = false;
            p.m_noClipping = false;
        }

        private static void RestoreGhostPlacementFlags(Player player)
        {
            if (!s_flagsActive) return;
            GameObject ghost = PlacementGhostRef(player);
            Piece p = ghost != null ? ghost.GetComponent<Piece>() : null;
            if (p != null && p == s_flagsPiece)
            {
                p.m_groundPiece = s_savedFlags.GroundPiece;
                p.m_groundOnly = s_savedFlags.GroundOnly;
                p.m_cultivatedGroundOnly = s_savedFlags.CultivatedGroundOnly;
                p.m_vegetationGroundOnly = s_savedFlags.VegetationGroundOnly;
                p.m_notOnWood = s_savedFlags.NotOnWood;
                p.m_notOnTiltingSurface = s_savedFlags.NotOnTiltingSurface;
                p.m_noInWater = s_savedFlags.NoInWater;
                p.m_noClipping = s_savedFlags.NoClipping;
            }
            s_flagsActive = false;
            s_flagsPiece = null;
        }

        // ============ Контейнер на тролле ============

        [HarmonyPatch(typeof(Character), "Awake")]
        [HarmonyPostfix]
        private static void Character_Awake_Postfix(Character __instance)
        {
            if (__instance == null || !IsTroll(__instance)) return;
            if (__instance.GetComponent<TrollPiecesContainer>() == null)
                __instance.gameObject.AddComponent<TrollPiecesContainer>();
        }

        // ============ Рейкаст цели ============

        [HarmonyPatch(typeof(Player), "PieceRayTest")]
        [HarmonyPrefix]
        private static bool PieceRayTest_Prefix(
            Player __instance,
            ref bool __result,
            out Vector3 point, out Vector3 normal, out Piece piece,
            out Heightmap heightmap, out Collider waterSurface,
            bool water)
        {
            point = Vector3.zero;
            normal = Vector3.up;
            piece = null;
            heightmap = null;
            waterSurface = null;

            TrollBuildContext.Reset();
            if (TrollBuildConstants.IsDedicatedServer || GameCamera.instance == null) return true;

            GameObject ghost = PlacementGhostRef(__instance);
            Piece ghostPiece = ghost != null ? ghost.GetComponent<Piece>() : null;

            int layerMask = water ? PlaceWaterRayMaskRef(__instance) : PlaceRayMaskRef(__instance);
            if (layerMask == 0)
                layerMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid", "terrain", "vehicle");
            int combined = layerMask | LayerMask.GetMask("character", "character_net");

            float maxDist = __instance.m_maxPlaceDistance;
            if (ghostPiece != null) maxDist += ghostPiece.m_extraPlacementDistance;
            maxDist += TrollBuildConstants.TrollExtraReach;

            Vector3 eyePos = __instance.m_eye != null ? __instance.m_eye.position : GameCamera.instance.transform.position;

            RaycastHit[] hits = Physics.RaycastAll(
                GameCamera.instance.transform.position,
                GameCamera.instance.transform.forward,
                60f, combined);
            if (hits.Length == 0) return true;
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            foreach (RaycastHit hit in hits)
            {
                Collider col = hit.collider;
                if (col == null) continue;
                if (Vector3.Distance(eyePos, hit.point) > maxDist) continue;
                if (col.GetComponentInParent<Player>() != null) continue; // своя капсула
                if (col.gameObject.layer == LayerMask.NameToLayer("Water")) continue;

                // Б: деталь, уже стоящая на тролле
                TrollPieceTag tag = col.GetComponentInParent<TrollPieceTag>();
                if (tag != null && tag.Container != null)
                {
                    Piece hitPiece = col.GetComponentInParent<Piece>();
                    TrollBuildContext.SetTargetPiece(tag.Container, hitPiece);
                    ClearGhostPlacementFlags(__instance);

                    point = hit.point;
                    normal = hit.normal;
                    piece = hitPiece;
                    __result = true;
                    return false;
                }

                // А: прирученный тролль, точка в зоне строительства на спине
                Character ch = col.GetComponentInParent<Character>();
                if (ch != null && IsTroll(ch) && ch.IsTamed())
                {
                    TrollPiecesContainer container = ch.GetComponent<TrollPiecesContainer>();
                    if (container == null) container = ch.gameObject.AddComponent<TrollPiecesContainer>();

                    if (container.IsInBuildZone(hit.point))
                    {
                        container.InitUUID();
                        container.EnsureBuildRoot();
                        TrollBuildContext.SetTargetSkin(container);
                        ClearGhostPlacementFlags(__instance);

                        point = hit.point;
                        normal = container.BuildRoot != null ? container.BuildRoot.up : Vector3.up;
                        piece = null;
                        __result = true;
                        return false;
                    }
                    continue; // тролль вне зоны — смотрим сквозь него
                }

                // В: обычная статичная геометрия — ваниль
                if (col.attachedRigidbody == null) return true;
            }
            return true;
        }

        // ============ Вращение ghost в системе тролля (транспайлер) ============

        public static Quaternion GetTrollGhostRotation(float x, float y, float z)
        {
            Quaternion local = Quaternion.Euler(x, y, z);
            if (TrollBuildContext.IsActive)
            {
                Transform root = TrollBuildContext.BuildRoot;
                if (root != null) return root.rotation * local;
            }
            return local;
        }

        [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> UpdatePlacementGhost_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var eulerMethod = AccessTools.Method(typeof(Quaternion), nameof(Quaternion.Euler),
                new[] { typeof(float), typeof(float), typeof(float) });
            var customMethod = AccessTools.Method(typeof(TrollBuildingPatches), nameof(GetTrollGhostRotation));

            foreach (var ins in instructions)
            {
                if (ins.Calls(eulerMethod))
                    yield return new CodeInstruction(OpCodes.Call, customMethod);
                else
                    yield return ins;
            }
        }

        // ============ Лок ghost + форс статуса ============

        private static Vector3 s_lockedLocalPos = Vector3.zero;
        private static Quaternion s_lockedLocalRot = Quaternion.identity;
        private static TrollPiecesContainer s_lockContainer;
        private static string s_lockGhostName = "";
        private static int s_lastPlaceRot = -1;
        private static int s_lastSnapIndex = -2;
        private static bool s_hasLockedPosition;

        [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
        [HarmonyPostfix]
        private static void UpdatePlacementGhost_Postfix(Player __instance)
        {
            if (TrollBuildConstants.IsDedicatedServer) return;
            RestoreGhostPlacementFlags(__instance);

            GameObject ghost = PlacementGhostRef(__instance);

            if (!TrollBuildContext.IsActive || ghost == null)
            {
                s_hasLockedPosition = false;
                s_lockContainer = null;
                return;
            }

            var container = TrollBuildContext.Container;
            Transform root = container.BuildRoot;
            if (root == null)
            {
                s_hasLockedPosition = false;
                s_lockContainer = null;
                return;
            }

            if (!ghost.activeInHierarchy) ghost.SetActive(true);

            int placeRotation = PlaceRotationRef(__instance);
            int snapIndex = ManualSnapPointRef(__instance);

            Vector2 mouseDelta = ZInput.GetMouseDelta();
            bool aimInput = mouseDelta.sqrMagnitude > 0.0001f
                || Mathf.Abs(ZInput.GetJoyRightStickX()) > 0.05f
                || Mathf.Abs(ZInput.GetJoyRightStickY()) > 0.05f
                || MoveDirRef(__instance).sqrMagnitude > 0.001f;

            bool changed = aimInput
                || placeRotation != s_lastPlaceRot
                || snapIndex != s_lastSnapIndex
                || s_lockContainer != container
                || s_lockGhostName != ghost.name
                || !s_hasLockedPosition;

            if (changed)
            {
                s_lockedLocalPos = root.InverseTransformPoint(ghost.transform.position);
                s_lockedLocalRot = Quaternion.Inverse(root.rotation) * ghost.transform.rotation;
                s_lastPlaceRot = placeRotation;
                s_lastSnapIndex = snapIndex;
                s_lockContainer = container;
                s_lockGhostName = ghost.name;
                s_hasLockedPosition = true;
            }

            ghost.transform.position = root.TransformPoint(s_lockedLocalPos);
            ghost.transform.rotation = root.rotation * s_lockedLocalRot;

            // Деталь-цель без опоры — строить сверху нельзя
            if (TrollBuildContext.TargetType == TrollBuildTargetType.TrollPiece && TrollBuildContext.TargetPiece != null)
            {
                WearNTear targetWnt = TrollBuildContext.TargetPiece.GetComponent<WearNTear>();
                if (targetWnt != null && !targetWnt.m_supports)
                {
                    PlacementStatusRef(__instance) = Player.PlacementStatus.Invalid;
                    Piece gp = ghost.GetComponent<Piece>();
                    if (gp != null) gp.SetInvalidPlacementHeightlight(true);
                    return;
                }
            }

            var status = PlacementStatusRef(__instance);
            if (status != Player.PlacementStatus.PrivateZone
                && status != Player.PlacementStatus.NoBuildZone
                && status != Player.PlacementStatus.Invalid)
            {
                PlacementStatusRef(__instance) = Player.PlacementStatus.Valid;
                Piece gp2 = ghost.GetComponent<Piece>();
                if (gp2 != null) gp2.SetInvalidPlacementHeightlight(false);
            }
        }

        // ============ Байпас клиппинга ============

        [HarmonyPatch(typeof(Player), "TestGhostClipping")]
        [HarmonyPrefix]
        private static bool TestGhostClipping_Prefix(ref bool __result)
        {
            if (TrollBuildContext.IsActive)
            {
                __result = false;
                return false;
            }
            return true;
        }

        // ============ Снап-сетка спины ============

        [HarmonyPatch(typeof(Piece), "GetSnapPoints",
            new[] { typeof(Vector3), typeof(float), typeof(List<Transform>), typeof(List<Piece>) })]
        [HarmonyPostfix]
        private static void Piece_GetSnapPoints_Postfix(List<Transform> points)
        {
            if (TrollBuildConstants.IsDedicatedServer) return;
            if (!TrollBuildContext.IsActive) return;
            var container = TrollBuildContext.Container;
            if (container == null || container.DeckSnapPoints.Count == 0) return;
            points.AddRange(container.DeckSnapPoints);
        }

        // ============ Привязка после постановки ============

        [HarmonyPatch(typeof(Player), "PlacePiece")]
        [HarmonyPostfix]
        private static void PlacePiece_Postfix(Player __instance, Piece piece, Vector3 pos, Quaternion rot)
        {
            if (!TrollBuildContext.IsActive || TrollBuildContext.Container == null)
            {
                TrollBuildContext.Reset();
                s_hasLockedPosition = false;
                s_lockContainer = null;
                return;
            }

            var container = TrollBuildContext.Container;
            Transform root = container.BuildRoot;
            if (root == null) { TrollBuildContext.Reset(); return; }

            container.InitUUID();
            if (string.IsNullOrEmpty(container.TrollUUID)) { TrollBuildContext.Reset(); return; }

            // Источник истины — фактический трансформ поставленной детали
            Piece placed = null;
            var placedList = AccessTools.StaticFieldRefAccess<List<IPlaced>>(typeof(Player), "m_placed");
            if (placedList != null)
            {
                foreach (var item in placedList)
                    if (item is Piece p && !Player.IsPlacementGhost(p.gameObject)) { placed = p; break; }
            }
            if (placed == null)
            {
                Collider[] cols = Physics.OverlapSphere(pos, 2.5f);
                float min = float.MaxValue;
                foreach (var c in cols)
                {
                    Piece p = c.GetComponentInParent<Piece>();
                    if (p == null || Player.IsPlacementGhost(p.gameObject)) continue;
                    if (piece != null && !p.gameObject.name.StartsWith(piece.gameObject.name)) continue;
                    float d = Vector3.Distance(p.transform.position, pos);
                    if (d < min) { min = d; placed = p; }
                }
            }

            if (placed != null)
            {
                ZNetView nview = placed.GetComponent<ZNetView>();
                if (nview != null && nview.IsValid())
                {
                    Vector3 localPos = root.InverseTransformPoint(placed.transform.position);
                    Quaternion localRot = Quaternion.Inverse(root.rotation) * placed.transform.rotation;

                    ZDO zdo = nview.GetZDO();
                    zdo.Set(TrollBuildConstants.HashTrollUUID, container.TrollUUID);
                    zdo.Set(TrollBuildConstants.HashLocalPos, localPos);
                    zdo.Set(TrollBuildConstants.HashLocalRotQ, localRot);
                    zdo.Set(TrollBuildConstants.HashHasLocal, true);

                    if (TrollBuildContext.TargetType == TrollBuildTargetType.TrollPiece
                        && TrollBuildContext.TargetPiece != null)
                    {
                        ZNetView parentNv = TrollBuildContext.TargetPiece.GetComponent<ZNetView>();
                        if (parentNv != null && parentNv.GetZDO() != null)
                            zdo.Set(TrollBuildConstants.ParentPieceHashPair, parentNv.GetZDO().m_uid);
                    }

                    container.RegisterPiece(nview, localPos, localRot);
                }
            }

            TrollBuildContext.Reset();
            s_hasLockedPosition = false;
            s_lockContainer = null;
        }

        // ============ Износ/поддержка ============

        [HarmonyPatch(typeof(WearNTear), "UpdateSupport")]
        [HarmonyPrefix]
        private static bool WearNTear_UpdateSupport_Prefix(WearNTear __instance)
        {
            if (__instance == null) return true;
            TrollPieceTag tag = __instance.GetComponent<TrollPieceTag>();
            if (tag != null && tag.Container != null)
            {
                TrollBuildSupport.CalculateTrollPieceSupport(__instance);
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(WearNTear), "OnDestroy")]
        [HarmonyPostfix]
        private static void WearNTear_OnDestroy_Postfix(WearNTear __instance)
        {
            if (__instance == null) return;
            TrollPieceTag tag = __instance.GetComponent<TrollPieceTag>();
            if (tag != null && tag.Container != null)
                tag.Container.OnPieceDestroyed(__instance);
        }

        // ============ Сеть / загрузка ============

        [HarmonyPatch(typeof(ZSyncTransform), "ClientSync")]
        [HarmonyPrefix]
        private static bool ZSyncTransform_ClientSync_Prefix(ZSyncTransform __instance)
        {
            if (__instance != null && __instance.GetComponent<TrollPieceTag>() != null) return false;
            return true;
        }

        [HarmonyPatch(typeof(ZNetView), "Awake")]
        [HarmonyPostfix]
        private static void ZNetView_Awake_Postfix(ZNetView __instance)
        {
            if (__instance == null) return;
            ZDO zdo = __instance.GetZDO();
            if (zdo == null) return;
            if (__instance.GetComponent<TrollPieceTag>() != null) return;

            string trollUUID = zdo.GetString(TrollBuildConstants.HashTrollUUID, "");
            if (string.IsNullOrEmpty(trollUUID)) return;

            if (TrollRegistry.TryGetTroll(trollUUID, out var container))
                TrollPieceAttachmentQueue.Attach(container, __instance);
            else
                TrollPieceAttachmentQueue.Enqueue(trollUUID, __instance);
        }

        // ============ Перенос игрока (troll-glue) ============

        [HarmonyPatch(typeof(Character), "ApplyGroundForce")]
        [HarmonyPrefix]
        private static bool ApplyGroundForce_Prefix(Character __instance, ref Vector3 vel, Vector3 targetVel)
        {
            if (__instance == null) return true;
            if (!__instance.IsOnGround()) return true; // в воздухе не таскаем (семантика корабля)

            Rigidbody groundBody = LastGroundBodyRef(__instance);
            TrollPiecesContainer container = null;

            if (groundBody != null)
                container = groundBody.GetComponent<TrollPiecesContainer>();
            if (container == null)
            {
                Collider gc = LastGroundColliderRef(__instance);
                if (gc != null) container = gc.GetComponentInParent<TrollPiecesContainer>();
            }
            if (container == null) return true; // земля/корабль — ваниль

            Rigidbody body = BodyRef(__instance);
            Transform root = container.BuildRoot;
            if (body == null || root == null) return true;

            // 1. Линейный перенос (Y гасим — вертикаль держит контакт капсулы)
            Vector3 pv = container.PlatformVelocity;
            pv.y = 0f;
            vel += pv;

            // 2. Вращательный перенос ω×r — то, чего нет у корабля в GetPointVelocity
            Vector3 av = container.PlatformAngularVelocity;
            if (av.sqrMagnitude > 0f)
            {
                Vector3 r = body.position - container.transform.position;
                Vector3 rotVel = Vector3.Cross(av, r);
                if (rotVel.sqrMagnitude < 400f) vel += rotVel; // clamp 20 м/с
            }

            // 3. Attach-точка — как корабль в ванили
            if (targetVel.magnitude > 0.01f)
            {
                LastAttachBodyRef(__instance) = null;
            }
            else
            {
                if (LastAttachBodyRef(__instance) != groundBody)
                {
                    LastAttachBodyRef(__instance) = groundBody;
                    LastAttachPosRef(__instance) = root.InverseTransformPoint(body.position);
                }
                Vector3 target = root.TransformPoint(LastAttachPosRef(__instance));
                Vector3 delta = target - body.position;
                if (delta.magnitude < 4f)
                {
                    delta.y = 0f;
                    vel += delta * 10f;
                    if (delta.magnitude > 1f) // жёсткая дуга при рывке
                        body.position = new Vector3(target.x, body.position.y, target.z);
                }
                else
                {
                    LastAttachBodyRef(__instance) = null;
                }
            }
            return false;
        }

        // ============ Ограничение скорости поворота тролля с постройками ============

        [HarmonyPatch(typeof(Character), "UpdateRotation")]
        [HarmonyPrefix]
        private static void Character_UpdateRotation_Prefix(Character __instance, ref float turnSpeed)
        {
            if (__instance == null || !IsTroll(__instance)) return;
            TrollPiecesContainer container = __instance.GetComponent<TrollPiecesContainer>();
            if (container == null || container.PieceCount == 0) return;
            if (turnSpeed > TrollBuildConstants.MaxTurnSpeedWithPieces)
                turnSpeed = TrollBuildConstants.MaxTurnSpeedWithPieces;
        }
    }

    #endregion
}