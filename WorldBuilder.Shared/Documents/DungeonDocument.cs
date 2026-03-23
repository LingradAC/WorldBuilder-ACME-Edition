using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using MemoryPack;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Shared.Documents {

    [MemoryPackable]
    public partial class DungeonData {
        public ushort LandblockKey;
        public List<DungeonCellData> Cells = new();
        /// <summary>Generators/items/portals to push to ACE landblock_instance on export (when AceDb configured).</summary>
        public List<DungeonInstancePlacement> InstancePlacements = new();
    }

    [MemoryPackable]
    public partial class DungeonCellData {
        public ushort CellNumber;
        public ushort EnvironmentId;
        public ushort CellStructure;
        public Vector3 Origin;
        public Quaternion Orientation = Quaternion.Identity;
        public uint Flags;
        public uint RestrictionObj;
        public List<ushort> Surfaces = new();
        public List<DungeonCellPortalData> CellPortals = new();
        public List<ushort> VisibleCells = new();
        public List<DungeonStabData> StaticObjects = new();
    }

    [MemoryPackable]
    public partial class DungeonCellPortalData {
        public ushort OtherCellId;
        public ushort PolygonId;
        public ushort OtherPortalId;
        public ushort Flags;
    }

    [MemoryPackable]
    public partial class DungeonStabData {
        public uint Id;
        public Vector3 Origin;
        public Quaternion Orientation = Quaternion.Identity;
    }

    /// <summary>
    /// A generator, item, or portal placement for a dungeon cell to be written to the ACE
    /// landblock_instance table on export. Lets server operators place spawns in dungeons
    /// and sync them via the same DB connection used for reposition.
    /// </summary>
    [MemoryPackable]
    public partial class DungeonInstancePlacement {
        public uint WeenieClassId { get; set; }
        public ushort CellNumber { get; set; }
        public Vector3 Origin { get; set; }
        public Quaternion Orientation { get; set; } = Quaternion.Identity;
    }

    public partial class DungeonDocument : BaseDocument {
        public override string Type => nameof(DungeonDocument);

        [MemoryPackInclude]
        private DungeonData _data = new();

        public ushort LandblockKey {
            get => _data.LandblockKey;
            set => _data.LandblockKey = value;
        }

        public List<DungeonCellData> Cells => _data.Cells;

        /// <summary>
        /// Generators, items, or portals to push to the ACE landblock_instance table on export
        /// when AceDb is configured. Enables server operators to place spawns in dungeons.
        /// </summary>
        public List<DungeonInstancePlacement> InstancePlacements => _data.InstancePlacements;

        private ushort _nextCellNumber = 0x0100;
        private readonly Queue<ushort> _recycledCellNumbers = new();

        public DungeonDocument(ILogger logger) : base(logger) {
        }

        public void SetLandblockKey(ushort key) {
            _data.LandblockKey = key;
            Id = $"dungeon_{key:X4}";
        }

        protected override async Task<bool> InitInternal(IDatReaderWriter datreader, DocumentManager documentManager) {
            // Parse landblock key from document Id (format: "dungeon_XXXX")
            if (LandblockKey == 0 && Id.StartsWith("dungeon_")) {
                var hex = Id.Replace("dungeon_", "");
                if (ushort.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsedKey)) {
                    _data.LandblockKey = parsedKey;
                }
            }

            if (Cells.Count == 0 && LandblockKey != 0) {
                LoadCellsFromDat(datreader);
            }
            if (Cells.Count > 0) {
                _nextCellNumber = (ushort)(Cells.Max(c => c.CellNumber) + 1);
            }
            ClearDirty();
            return true;
        }

        protected override byte[] SaveToProjectionInternal() {
            return MemoryPackSerializer.Serialize(_data);
        }

        protected override bool LoadFromProjectionInternal(byte[] projection) {
            try {
                _data = MemoryPackSerializer.Deserialize<DungeonData>(projection) ?? new();
            }
            catch (MemoryPack.MemoryPackSerializationException) {
                _logger.LogWarning("[DungeonDoc] Project cache has incompatible format (schema changed), will reload from DAT");
                _data = new();
            }
            if (Cells.Count > 0) {
                _nextCellNumber = (ushort)(Cells.Max(c => c.CellNumber) + 1);
            }
            _recycledCellNumbers.Clear();
            return true;
        }

        protected override Task<bool> SaveToDatsInternal(IDatReaderWriter datwriter, int iteration = 0) {
            if (Cells.Count == 0) {
                _logger.LogWarning("[DungeonDoc] Nothing to export: dungeon has no cells");
                return Task.FromResult(true);
            }

            uint lbId = LandblockKey;
            uint lbEntryId = (lbId << 16) | 0xFFFF;
            uint lbiId = (lbId << 16) | 0xFFFE;

            // Step 1: Ensure a LandBlock (0xFFFF) terrain entry exists.
            // Existing outdoor landblocks already have one; empty ones need a stub.
            bool hasLandBlock = datwriter.TryGet<LandBlock>(lbEntryId, out _);
            if (!hasLandBlock) {
                var lb = new LandBlock { Id = lbEntryId };
                if (!datwriter.TrySave(lb, iteration)) {
                    _logger.LogError("[DungeonDoc] Failed to create LandBlock 0x{Id:X8}", lbEntryId);
                    return Task.FromResult(false);
                }
                _logger.LogInformation("[DungeonDoc] Created new LandBlock 0x{Id:X8} (dungeon-only landblock, no prior terrain)", lbEntryId);
            }

            // Step 2: Get or create LandBlockInfo (0xFFFE).
            // If it exists (landblock has buildings/objects), preserve them — only update NumCells.
            bool isNewLbi = !datwriter.TryGet<LandBlockInfo>(lbiId, out var lbi);
            if (isNewLbi) {
                lbi = new LandBlockInfo { Id = lbiId };
            }
            uint prevNumCells = lbi.NumCells;
            int existingBuildings = lbi.Buildings?.Count ?? 0;
            int existingObjects = lbi.Objects?.Count ?? 0;

            // Step 2.5: Auto-compute VisibleCells (stab lists) if any are empty
            bool anyEmpty = Cells.Any(c => c.VisibleCells.Count == 0);
            if (anyEmpty) {
                int updated = ComputeVisibleCells();
                _logger.LogInformation("[DungeonDoc] Auto-computed VisibleCells for {Updated}/{Total} cells before export", updated, Cells.Count);
            }

            // Step 2.6: Ensure portal flags (PortalSide) are correct before export.
            // The AC client uses portal_side to determine which half-space of the
            // portal polygon plane is the cell interior. Incorrect values cause
            // broken rendering and traversal.
            int flagsFixed = RecomputePortalFlags(datwriter);
            if (flagsFixed > 0)
                _logger.LogInformation("[DungeonDoc] Fixed {Count} portal flag(s) before export", flagsFixed);

            // Step 3: Save all EnvCells
            var envCells = ToEnvCells(forDatExport: true);
            SanitizePortalTopologyForExport(datwriter, envCells);
            SanitizeEnvCellSurfacesForExport(datwriter, envCells);
            SanitizeEnvCellStaticsForExport(datwriter, envCells);
            ReorderCellPortalsToMatchCellStruct(datwriter, envCells);
            RewriteOtherPortalIndices(envCells);
            PopulateVisibilityStabs(envCells);
            int saved = 0;
            foreach (var envCell in envCells) {
                if (!datwriter.TrySave(envCell, iteration)) {
                    _logger.LogError("[DungeonDoc] Failed to save EnvCell 0x{CellId:X8}", envCell.Id);
                    return Task.FromResult(false);
                }
                saved++;
            }

            // Step 4: Update NumCells and add dungeon BuildingInfo to the LBI.
            // Original dungeons have buildings=0 in their LBI. Cell visibility is handled
            // through the EnvCell VisibleCells list (light_list.data in the client),
            // not through BuildingInfo/BuildingPortal stab lists.
            var maxCellNum = Cells.Max(c => c.CellNumber);
            lbi.NumCells = (uint)(maxCellNum - 0x00FF);

            if (!datwriter.TrySave(lbi, iteration)) {
                _logger.LogError("[DungeonDoc] Failed to save LandBlockInfo 0x{InfoId:X8}", lbiId);
                return Task.FromResult(false);
            }

            _logger.LogInformation(
                "[DungeonDoc] Exported LB {LB:X4}: {Saved}/{Total} cells saved, " +
                "LBI 0x{LbiId:X8} (new={IsNew}, NumCells: {Prev}->{New}, buildings={Bldg}, objects={Obj}), " +
                "LandBlock 0x{LbId:X8} (existed={HasLB})",
                LandblockKey, saved, envCells.Count,
                lbiId, isNewLbi, prevNumCells, lbi.NumCells, existingBuildings, existingObjects,
                lbEntryId, hasLandBlock);

            // Diagnostic: compare with a known original dungeon's LBI and cell
            uint refLbiId = 0x01D9FFFE;
            if (datwriter.TryGet<LandBlockInfo>(refLbiId, out var refLbi)) {
                string bldgInfo = "";
                if (refLbi.Buildings?.Count > 0) {
                    var b = refLbi.Buildings[0];
                    string portalInfo = "";
                    if (b.Portals?.Count > 0) {
                        var p = b.Portals[0];
                        var refStabs = p.StabList.Take(5).Select(s => $"0x{s:X4}");
                        portalInfo = $" portal0=[other=0x{p.OtherCellId:X4},otherP={p.OtherPortalId},flags={p.Flags},stabs={p.StabList.Count} ids=[{string.Join(",", refStabs)}]]";
                    }
                    bldgInfo = $" bldg0=[model=0x{b.ModelId:X8},leaves={b.NumLeaves},portals={b.Portals?.Count ?? 0}{portalInfo}]";
                }
                _logger.LogInformation(
                    "[DungeonDoc] REFERENCE LBI 0x{Id:X8}: numCells={NC}, buildings={BC}, objects={OC}{BldgInfo}",
                    refLbiId, refLbi.NumCells, refLbi.Buildings?.Count ?? 0, refLbi.Objects?.Count ?? 0, bldgInfo);
            }

            // Binary format diagnostic: pack a reference cell and compare sizes
            // to detect any field width mismatches between DatReaderWriter and the client
            uint refCellId = 0x01D90100;
            if (datwriter.TryGet<EnvCell>(refCellId, out var refCell)) {
                int rawSize = -1;

                // Pack via DatReaderWriter and measure size
                var packBuf = new byte[8192];
                var packWriter = new DatReaderWriter.Lib.IO.DatBinWriter(packBuf);
                refCell.Pack(packWriter);
                int drwSize = packWriter.Offset;

                // Calculate expected client size manually:
                // Header: 4(id)
                // Flags: 4, CellId: 4, numSurf: 1, numPort: 1, numVis: 2
                // Surfaces: 2*n, EnvId: 2, CellStruct: ?, Frame: 28
                // Portals: 8*n, VisCells: 2*n
                // Stabs (if flag 0x2): 4 + 32*n
                // RestrictionObj (if flag 0x8): 4
                int nSurf = refCell.Surfaces.Count;
                int nPort = refCell.CellPortals.Count;
                int nVis = refCell.VisibleCells.Count;
                int nStab = refCell.StaticObjects.Count;
                bool hasStabs = ((uint)refCell.Flags & 2) != 0;
                bool hasRestrict = ((uint)refCell.Flags & 8) != 0;

                int expectedWith2 = 4 + 4 + 4 + 1 + 1 + 2 + (2 * nSurf) + 2 + 2 + 28 +
                    (8 * nPort) + (2 * nVis) +
                    (hasStabs ? 4 + (32 * nStab) : 0) +
                    (hasRestrict ? 4 : 0);
                int expectedWith1 = expectedWith2 - 1;

                _logger.LogInformation(
                    "[DungeonDoc] BINARY FORMAT TEST cell 0x{Id:X8}: rawDatSize={Raw}, drwPackSize={DRW}, " +
                    "expectedWith2ByteCS={Exp2}, expectedWith1ByteCS={Exp1} " +
                    "(surfaces={S}, portals={P}, visCells={V}, stabs={St}, flags=0x{F:X})",
                    refCellId, rawSize, drwSize, expectedWith2, expectedWith1,
                    nSurf, nPort, nVis, nStab, (uint)refCell.Flags);
            }

            // Verify: read back first cell and LBI to confirm persistence
            uint firstCellId = (lbId << 16) | 0x0100;
            bool cellOk = datwriter.TryGet<EnvCell>(firstCellId, out var verifyCell);
            bool lbiOk = datwriter.TryGet<LandBlockInfo>(lbiId, out var verifyLbi);
            int verifyStabs = cellOk ? verifyCell!.StaticObjects?.Count ?? 0 : 0;
            int verifyPortals = cellOk ? verifyCell!.CellPortals?.Count ?? 0 : 0;
            int verifyVis = cellOk ? verifyCell!.VisibleCells?.Count ?? 0 : 0;
            uint verifyFlags = cellOk ? (uint)verifyCell!.Flags : 0;
            string stabSample = "";
            if (cellOk && verifyCell!.StaticObjects?.Count > 0) {
                var first3 = verifyCell.StaticObjects.Take(3).Select(s => $"0x{s.Id:X8}");
                stabSample = $" stabIds=[{string.Join(",", first3)}]";
            }
            string ourPortalSample = "";
            if (cellOk && verifyCell!.CellPortals?.Count > 0) {
                var pSample = verifyCell.CellPortals.Take(3).Select(p =>
                    $"poly={p.PolygonId}→cell=0x{p.OtherCellId:X4},otherP={p.OtherPortalId},flags={p.Flags}");
                ourPortalSample = $" portals=[{string.Join(" | ", pSample)}]";
            }
            _logger.LogInformation(
                "[DungeonDoc] Verify LB {LB:X4}: cell 0x{CellId:X8} exists={CellOk} (env=0x{Env:X4}, " +
                "flags=0x{Flags:X}, portals={Portals}, visCells={Vis}, stabs={Stabs}{StabSample}{PortalSample}), " +
                "LBI exists={LbiOk} (numCells={Num})",
                LandblockKey, firstCellId, cellOk,
                cellOk ? verifyCell!.EnvironmentId : 0,
                verifyFlags, verifyPortals, verifyVis, verifyStabs, stabSample, ourPortalSample,
                lbiOk,
                lbiOk ? verifyLbi!.NumCells : 0);

            // Verify second cell exists and log VisibleCells for first cell
            uint secondCellId = (lbId << 16) | 0x0101;
            bool cell2Ok = datwriter.TryGet<EnvCell>(secondCellId, out var verifyCell2);
            string visListStr = "";
            if (cellOk && verifyCell!.VisibleCells?.Count > 0) {
                visListStr = string.Join(",", verifyCell.VisibleCells.Select(v => $"0x{v:X4}"));
            }
            _logger.LogInformation(
                "[DungeonDoc] Cell2 0x{Id:X8} exists={Ok} (env=0x{Env:X4}, portals={P}), " +
                "Cell1 visCells=[{VisList}]",
                secondCellId, cell2Ok,
                cell2Ok ? verifyCell2!.EnvironmentId : 0,
                cell2Ok ? verifyCell2!.CellPortals?.Count ?? 0 : 0,
                visListStr);

            return Task.FromResult(true);
        }

        private void SanitizeEnvCellSurfacesForExport(IDatReaderWriter datwriter, List<EnvCell> envCells) {
            uint? fallbackSurfaceId = ResolveFallbackSurfaceId(datwriter);
            if (!fallbackSurfaceId.HasValue) {
                _logger.LogWarning("[DungeonDoc] Surface sanitizer: no valid fallback surface found; leaving surfaces unchanged");
                return;
            }

            int filledEmpty = 0;
            int mismatchObserved = 0;

            foreach (var envCell in envCells) {
                int requiredSlots = GetRequiredSurfaceSlots(datwriter, envCell.EnvironmentId, envCell.CellStructure);

                // Conservative export fix: only fill truly empty surface lists.
                // Existing per-cell surface values may be valid runtime references
                // even when they do not resolve through the simple Surface chain check.
                if (requiredSlots > 0 && envCell.Surfaces.Count == 0) {
                    envCell.Surfaces.AddRange(Enumerable.Repeat((ushort)fallbackSurfaceId.Value, requiredSlots));
                    filledEmpty++;
                }
                else if (requiredSlots > 0 && envCell.Surfaces.Count != requiredSlots) {
                    mismatchObserved++;
                }
            }

            if (filledEmpty > 0 || mismatchObserved > 0) {
                _logger.LogInformation(
                    "[DungeonDoc] Surface sanitizer: filled {FilledEmpty} empty surface list(s); observed {Mismatches} non-empty slot-count mismatch(es) (left unchanged)",
                    filledEmpty, mismatchObserved);
            }
        }

        private static int GetRequiredSurfaceSlots(IDatReaderWriter dats, ushort envId, ushort cellStruct) {
            uint envFileId = (uint)(envId | 0x0D000000);
            if (!dats.TryGet<DatReaderWriter.DBObjs.Environment>(envFileId, out var env)) return 0;
            if (!env.Cells.TryGetValue(cellStruct, out var cs)) return 0;

            var portalIds = cs.Portals != null ? new HashSet<ushort>(cs.Portals) : new HashSet<ushort>();
            int maxIndex = -1;
            foreach (var kvp in cs.Polygons) {
                if (portalIds.Contains(kvp.Key)) continue;
                if (kvp.Value.PosSurface > maxIndex) maxIndex = kvp.Value.PosSurface;
            }
            return maxIndex + 1;
        }

        private static uint? ResolveFallbackSurfaceId(IDatReaderWriter dats) {
            // Prefer the historical dungeon fallback.
            const uint preferred = 0x032A;
            if (dats.TryGet<Surface>(preferred, out _)) return preferred;

            foreach (var id in dats.Dats.Portal.GetAllIdsOfType<Surface>()) {
                if (dats.TryGet<Surface>(id, out _))
                    return id;
            }
            return null;
        }

        private void SanitizeEnvCellStaticsForExport(IDatReaderWriter datwriter, List<EnvCell> envCells) {
            int removed = 0;
            foreach (var envCell in envCells) {
                if (envCell.StaticObjects == null || envCell.StaticObjects.Count == 0) continue;
                int before = envCell.StaticObjects.Count;
                envCell.StaticObjects.RemoveAll(stab => {
                    try {
                        return !datwriter.TryGet<Setup>(stab.Id, out _);
                    }
                    catch (Exception ex) {
                        _logger.LogWarning("[DungeonDoc] Failed to read Setup 0x{Id:X8} from dat: {Message}", stab.Id, ex.Message);
                        return true;
                    }
                });
                removed += before - envCell.StaticObjects.Count;
            }

            if (removed > 0) {
                _logger.LogWarning("[DungeonDoc] Static sanitizer removed {Removed} invalid stab reference(s) before export", removed);
            }
        }

        private void SanitizePortalTopologyForExport(IDatReaderWriter datwriter, List<EnvCell> envCells) {
            var byId = envCells.ToDictionary(c => c.Id, c => c);
            var validPortalPolys = new Dictionary<uint, HashSet<ushort>>();
            var validAllPolys = new Dictionary<uint, HashSet<ushort>>();

            int removedInvalidPoly = 0;
            int removedDuplicate = 0;
            int removedDangling = 0;
            int fixedBacklinks = 0;

            (HashSet<ushort> portalPolys, HashSet<ushort> allPolys) getPolySets(EnvCell cell) {
                if (validPortalPolys.TryGetValue(cell.Id, out var cachedPortal) &&
                    validAllPolys.TryGetValue(cell.Id, out var cachedAll)) {
                    return (cachedPortal, cachedAll);
                }

                uint envFileId = (uint)(cell.EnvironmentId | 0x0D000000);
                var portalSet = new HashSet<ushort>();
                var allSet = new HashSet<ushort>();
                if (datwriter.TryGet<DatReaderWriter.DBObjs.Environment>(envFileId, out var env) &&
                    env.Cells.TryGetValue(cell.CellStructure, out var cs)) {
                    if (cs.Portals != null) {
                        foreach (var p in cs.Portals)
                            portalSet.Add(p);
                    }
                    if (cs.Polygons != null) {
                        foreach (var p in cs.Polygons.Keys)
                            allSet.Add(p);
                    }
                }
                validPortalPolys[cell.Id] = portalSet;
                validAllPolys[cell.Id] = allSet;
                return (portalSet, allSet);
            }

            foreach (var cell in envCells) {
                var (portalPolys, allPolys) = getPolySets(cell);
                var seenPoly = new HashSet<ushort>();
                var sanitized = new List<CellPortal>(cell.CellPortals.Count);
                foreach (var cp in cell.CellPortals) {
                    ushort poly = (ushort)cp.PolygonId;
                    if (!allPolys.Contains(poly) || !portalPolys.Contains(poly)) {
                        removedInvalidPoly++;
                        continue;
                    }
                    if (!seenPoly.Add(poly)) {
                        removedDuplicate++;
                        continue;
                    }
                    uint otherId = (cell.Id & 0xFFFF0000u) | cp.OtherCellId;
                    if (!byId.ContainsKey(otherId)) {
                        removedDangling++;
                        continue;
                    }
                    sanitized.Add(cp);
                }
                cell.CellPortals.Clear();
                cell.CellPortals.AddRange(sanitized);
            }

            // Ensure reciprocal portal links exist and are valid.
            foreach (var cell in envCells) {
                foreach (var cp in cell.CellPortals.ToList()) {
                    uint otherId = (cell.Id & 0xFFFF0000u) | cp.OtherCellId;
                    if (!byId.TryGetValue(otherId, out var otherCell)) continue;

                    ushort otherPoly = (ushort)cp.OtherPortalId;
                    var (otherPortalPolys, otherAllPolys) = getPolySets(otherCell);
                    if (!otherAllPolys.Contains(otherPoly) || !otherPortalPolys.Contains(otherPoly))
                        continue;

                    bool hasBack = otherCell.CellPortals.Any(p =>
                        p.OtherCellId == (ushort)(cell.Id & 0xFFFF) &&
                        (ushort)p.PolygonId == otherPoly &&
                        (ushort)p.OtherPortalId == (ushort)cp.PolygonId);
                    if (hasBack) continue;

                    // If polygon slot already used on other cell, do not force conflicting backlink.
                    bool otherPolyUsed = otherCell.CellPortals.Any(p => (ushort)p.PolygonId == otherPoly);
                    if (otherPolyUsed) continue;

                    otherCell.CellPortals.Add(new CellPortal {
                        OtherCellId = (ushort)(cell.Id & 0xFFFF),
                        PolygonId = otherPoly,
                        OtherPortalId = cp.PolygonId,
                        Flags = cp.Flags
                    });
                    fixedBacklinks++;
                }
            }

            if (removedInvalidPoly > 0 || removedDuplicate > 0 || removedDangling > 0 || fixedBacklinks > 0) {
                _logger.LogWarning(
                    "[DungeonDoc] Portal sanitizer: removed invalidPoly={InvalidPoly}, duplicate={Duplicate}, dangling={Dangling}; addedBacklinks={Backlinks}",
                    removedInvalidPoly, removedDuplicate, removedDangling, fixedBacklinks);
            }
        }

        /// <summary>
        /// Populates each EnvCell's StaticObjects (stab section, flag 0x2) with visibility
        /// entries so the AC client can preload adjacent cells for portal rendering.
        ///
        /// The client's CEnvCell::grab_visible_cells iterates the stab_list (StaticObjects
        /// section) and calls add_visible_cell(stab_list[i]) for each entry. This populates
        /// the visible_cell_table used by GetVisible(), which GetOtherCell() uses to resolve
        /// portal targets. Without stab entries, GetVisible returns NULL for all adjacent
        /// cells and every portal renders as a black void.
        ///
        /// Each visibility entry is a Stab with Id = full 32-bit cell ID and
        /// Frame = the referenced cell's position. The client tolerates non-cell IDs
        /// (they silently fail to load), so actual static objects can coexist.
        /// </summary>
        private void PopulateVisibilityStabs(List<EnvCell> envCells) {
            var cellPositions = envCells.ToDictionary(c => c.Id, c => c.Position);
            int totalAdded = 0;

            foreach (var cell in envCells) {
                uint blockMask = cell.Id & 0xFFFF0000u;
                var existingIds = new HashSet<uint>(cell.StaticObjects.Select(s => s.Id));

                foreach (var visibleCellNum in cell.VisibleCells) {
                    uint fullCellId = blockMask | visibleCellNum;
                    if (fullCellId == cell.Id) continue;
                    if (existingIds.Contains(fullCellId)) continue;

                    if (!cellPositions.TryGetValue(fullCellId, out var pos))
                        pos = new Frame();

                    cell.StaticObjects.Add(new Stab {
                        Id = fullCellId,
                        Frame = pos
                    });
                    totalAdded++;
                }

                if (cell.StaticObjects.Count > 0)
                    cell.Flags |= EnvCellFlags.HasStaticObjs;
            }

            if (totalAdded > 0) {
                _logger.LogInformation(
                    "[DungeonDoc] Visibility stabs: added {Count} cell-ID stab(s) for portal rendering preload",
                    totalAdded);
            }
        }

        /// <summary>
        /// Reorders each EnvCell's CellPortals to match the CellStruct.Portals ordering
        /// from the Environment file, and pads with placeholder entries for unconnected portals.
        ///
        /// The AC client's PView rendering loop iterates CellStruct portal polygons and
        /// EnvCell CellPortals in lockstep by array index. CellPortals[i] must correspond
        /// to CellStruct.Portals[i]. If the ordering doesn't match, the BSP portal
        /// visibility check pairs with the wrong CellPortal, causing portals to render
        /// as black voids.
        ///
        /// Must run BEFORE RewriteOtherPortalIndices (since this changes CellPortal ordering).
        /// </summary>
        private void ReorderCellPortalsToMatchCellStruct(IDatReaderWriter dats, List<EnvCell> envCells) {
            int reordered = 0;
            int padded = 0;

            foreach (var cell in envCells) {
                uint envFileId = (uint)(cell.EnvironmentId | 0x0D000000);
                if (!dats.TryGet<DatReaderWriter.DBObjs.Environment>(envFileId, out var env)) continue;
                if (!env.Cells.TryGetValue(cell.CellStructure, out var cs)) continue;
                if (cs.Portals == null || cs.Portals.Count == 0) continue;

                // CellStruct.Portals may contain raw indices OR polygon IDs depending
                // on how DatReaderWriter unpacked. Resolve to polygon IDs consistently.
                var resolvedPortalIds = ResolvePortalPolygonIds(cs);

                var portalsByPoly = new Dictionary<ushort, CellPortal>();
                foreach (var cp in cell.CellPortals) {
                    portalsByPoly.TryAdd((ushort)cp.PolygonId, cp);
                }

                var ordered = new List<CellPortal>(resolvedPortalIds.Count);
                bool needsReorder = false;

                for (int i = 0; i < resolvedPortalIds.Count; i++) {
                    ushort polyId = resolvedPortalIds[i];
                    if (portalsByPoly.TryGetValue(polyId, out var existing)) {
                        ordered.Add(existing);
                        if (i < cell.CellPortals.Count && (ushort)cell.CellPortals[i].PolygonId != polyId)
                            needsReorder = true;
                    }
                    else {
                        ordered.Add(new CellPortal {
                            PolygonId = polyId,
                            OtherCellId = 0xFFFF,
                            OtherPortalId = 0xFFFF,
                            Flags = 0
                        });
                        padded++;
                        needsReorder = true;
                    }
                }

                if (!needsReorder && ordered.Count == cell.CellPortals.Count)
                    continue;

                cell.CellPortals.Clear();
                cell.CellPortals.AddRange(ordered);
                reordered++;
            }

            if (reordered > 0 || padded > 0) {
                _logger.LogInformation(
                    "[DungeonDoc] Portal ordering: reordered {Reordered} cell(s) to match CellStruct.Portals, added {Padded} placeholder(s) for unconnected portals",
                    reordered, padded);
            }
        }

        /// <summary>
        /// Resolve CellStruct.Portals entries to polygon IDs. The DAT stores indices into
        /// the polygon array, but DatReaderWriter may expose them as raw values or resolved IDs.
        /// This handles both cases, matching PortalSnapper.GetPortalPolygonIds logic.
        /// </summary>
        private static List<ushort> ResolvePortalPolygonIds(DatReaderWriter.Types.CellStruct cs) {
            if (cs.Portals == null || cs.Portals.Count == 0)
                return new List<ushort>();

            ushort[]? sortedPolyKeys = null;
            var result = new List<ushort>(cs.Portals.Count);

            foreach (var portalRef in cs.Portals) {
                ushort resolvedId = portalRef;
                if (!cs.Polygons.ContainsKey(resolvedId)) {
                    if (sortedPolyKeys == null)
                        sortedPolyKeys = cs.Polygons.Keys.OrderBy(k => k).ToArray();
                    int idx = portalRef;
                    if (idx >= 0 && idx < sortedPolyKeys.Length)
                        resolvedId = sortedPolyKeys[idx];
                }
                result.Add(resolvedId);
            }
            return result;
        }

        /// <summary>
        /// Rewrites CellPortal.OtherPortalId from polygon IDs to portal array indices.
        /// The AC client uses other_portal_id as a direct array index into the other
        /// cell's portals[] array (see PView::OtherPortalClip, CEnvCell::check_building_transit).
        /// ConnectPortals and SanitizePortalTopologyForExport store polygon IDs in this
        /// field, which causes out-of-bounds crashes when the polygon ID exceeds the
        /// portal count. This method must run after all portal additions/removals are final.
        /// </summary>
        private void RewriteOtherPortalIndices(List<EnvCell> envCells) {
            var byId = envCells.ToDictionary(c => c.Id, c => c);
            int rewritten = 0;
            int clamped = 0;

            foreach (var cell in envCells) {
                ushort cellNum = (ushort)(cell.Id & 0xFFFF);

                foreach (var cp in cell.CellPortals) {
                    if (cp.OtherCellId == 0xFFFF) continue;
                    uint otherFullId = (cell.Id & 0xFFFF0000u) | cp.OtherCellId;
                    if (!byId.TryGetValue(otherFullId, out var otherCell)) continue;

                    int bestIndex = -1;
                    int backlinksFound = 0;

                    for (int j = 0; j < otherCell.CellPortals.Count; j++) {
                        if (otherCell.CellPortals[j].OtherCellId != cellNum) continue;
                        backlinksFound++;

                        if (backlinksFound == 1)
                            bestIndex = j;

                        if ((ushort)otherCell.CellPortals[j].PolygonId == cp.OtherPortalId) {
                            bestIndex = j;
                            break;
                        }
                    }

                    if (bestIndex >= 0) {
                        if (cp.OtherPortalId != (ushort)bestIndex) {
                            cp.OtherPortalId = (ushort)bestIndex;
                            rewritten++;
                        }
                    } else {
                        // No backlink found — clamp to 0 to prevent out-of-bounds crash.
                        // The client uses OtherPortalId as a direct array index into the
                        // other cell's portals[]; leaving it as a polygon ID (e.g. 31)
                        // when the other cell has only 2 portals causes a crash.
                        if (otherCell.CellPortals.Count > 0) {
                            cp.OtherPortalId = 0;
                        }
                        _logger.LogWarning(
                            "[DungeonDoc] Portal index rewriter: no backlink found for cell 0x{CellId:X4} → 0x{OtherId:X4} (poly {Poly}), clamped OtherPortalId to 0",
                            cellNum, cp.OtherCellId, (ushort)cp.PolygonId);
                        clamped++;
                    }
                }
            }

            if (rewritten > 0 || clamped > 0) {
                _logger.LogInformation(
                    "[DungeonDoc] Portal index rewriter: fixed {Count} OtherPortalId value(s) (polygon ID → array index), clamped {Clamped} missing backlink(s)",
                    rewritten, clamped);
            }
        }

        /// <summary>
        /// Force reload all cells from the DAT file, discarding any in-memory edits.
        /// </summary>
        public void ReloadFromDat(IDatReaderWriter dats) {
            LoadCellsFromDat(dats);
            if (Cells.Count > 0)
                _nextCellNumber = (ushort)(Cells.Max(c => c.CellNumber) + 1);
            _recycledCellNumbers.Clear();
            ClearDirty();
        }

        private void LoadCellsFromDat(IDatReaderWriter dats) {
            Cells.Clear();
            uint lbId = LandblockKey;
            uint lbiId = (lbId << 16) | 0xFFFE;

            if (!dats.TryGet<LandBlockInfo>(lbiId, out var lbi) || lbi.NumCells == 0)
                return;

            for (uint i = 0; i < lbi.NumCells; i++) {
                ushort cellNum = (ushort)(0x0100 + i);
                uint cellId = (lbId << 16) | cellNum;
                if (dats.TryGet<EnvCell>(cellId, out var envCell)) {
                    var origin = envCell.Position.Origin;
                    var dc = new DungeonCellData {
                        CellNumber = cellNum,
                        EnvironmentId = envCell.EnvironmentId,
                        CellStructure = envCell.CellStructure,
                        Origin = origin,
                        Orientation = envCell.Position.Orientation,
                        Flags = (uint)envCell.Flags,
                        RestrictionObj = envCell.RestrictionObj,
                    };
                    dc.Surfaces.AddRange(envCell.Surfaces);
                    if (envCell.CellPortals != null) {
                        foreach (var cp in envCell.CellPortals) {
                            dc.CellPortals.Add(new DungeonCellPortalData {
                                OtherCellId = cp.OtherCellId,
                                PolygonId = (ushort)cp.PolygonId,
                                OtherPortalId = (ushort)cp.OtherPortalId,
                                Flags = (ushort)cp.Flags
                            });
                        }
                    }
                    if (envCell.VisibleCells != null) dc.VisibleCells.AddRange(envCell.VisibleCells);
                    if (envCell.StaticObjects != null) {
                        foreach (var stab in envCell.StaticObjects) {
                            var stabOrigin = stab.Frame.Origin;
                            dc.StaticObjects.Add(new DungeonStabData {
                                Id = stab.Id,
                                Origin = stabOrigin,
                                Orientation = stab.Frame.Orientation
                            });
                        }
                    }
                    Cells.Add(dc);
                }
            }
        }

        public ushort AllocateCellNumber() {
            if (_recycledCellNumbers.Count > 0)
                return _recycledCellNumbers.Dequeue();
            return _nextCellNumber++;
        }

        public ushort AddCell(ushort environmentId, ushort cellStructure, Vector3 origin, Quaternion orientation, List<ushort> surfaces) {
            var cellNum = AllocateCellNumber();
            var cell = new DungeonCellData {
                CellNumber = cellNum,
                EnvironmentId = environmentId,
                CellStructure = cellStructure,
                Origin = origin,
                Orientation = orientation,
            };
            cell.Surfaces.AddRange(surfaces);
            Cells.Add(cell);
            MarkDirty();
            return cellNum;
        }

        public void ConnectPortals(ushort cellNumA, ushort polyIdA, ushort cellNumB, ushort polyIdB) {
            var cellA = Cells.FirstOrDefault(c => c.CellNumber == cellNumA);
            var cellB = Cells.FirstOrDefault(c => c.CellNumber == cellNumB);
            if (cellA == null || cellB == null) return;

            cellA.CellPortals.Add(new DungeonCellPortalData {
                OtherCellId = cellNumB,
                PolygonId = polyIdA,
                OtherPortalId = polyIdB,
                Flags = 0
            });

            cellB.CellPortals.Add(new DungeonCellPortalData {
                OtherCellId = cellNumA,
                PolygonId = polyIdB,
                OtherPortalId = polyIdA,
                Flags = 0
            });
            MarkDirty();
        }

        public void RemoveCell(ushort cellNumber) {
            var cell = Cells.FirstOrDefault(c => c.CellNumber == cellNumber);
            if (cell == null) return;

            foreach (var other in Cells) {
                other.CellPortals.RemoveAll(cp => cp.OtherCellId == cellNumber);
            }

            Cells.Remove(cell);
            _recycledCellNumbers.Enqueue(cellNumber);
            MarkDirty();
        }

        // NOTE: We intentionally preserve authored dungeon coordinates on export.
        // No export-time depth remapping is applied.

        /// <summary>
        /// Legacy hook kept for compatibility with telemetry/manifest callers.
        /// Export-depth correction is disabled to match retail-authored behavior.
        /// </summary>
        public float ComputeDatExportZLift() {
            return 0f;
        }

        /// <summary>Convert all document cells to EnvCell objects for rendering or DAT export.</summary>
        public List<EnvCell> ToEnvCells(bool forDatExport = false) {
            uint lbId = LandblockKey;
            var result = new List<EnvCell>();

            foreach (var dc in Cells) {
                uint fullCellId = (lbId << 16) | dc.CellNumber;
                var origin = dc.Origin;
                var envCell = new EnvCell {
                    Id = fullCellId,
                    EnvironmentId = dc.EnvironmentId,
                    CellStructure = dc.CellStructure,
                    Flags = (EnvCellFlags)dc.Flags,
                    RestrictionObj = dc.RestrictionObj,
                    Position = new Frame {
                        Origin = origin,
                        Orientation = dc.Orientation
                    }
                };

                envCell.Surfaces.AddRange(dc.Surfaces);
                foreach (var cp in dc.CellPortals) {
                    envCell.CellPortals.Add(new CellPortal {
                        OtherCellId = cp.OtherCellId,
                        PolygonId = cp.PolygonId,
                        OtherPortalId = cp.OtherPortalId,
                        Flags = (PortalFlags)cp.Flags
                    });
                }
                envCell.VisibleCells.AddRange(dc.VisibleCells);
                foreach (var stab in dc.StaticObjects) {
                    var stabOrigin = stab.Origin;
                    envCell.StaticObjects.Add(new Stab {
                        Id = stab.Id,
                        Frame = new Frame {
                            Origin = stabOrigin,
                            Orientation = stab.Orientation
                        }
                    });
                }

                if (dc.StaticObjects.Count > 0)
                    envCell.Flags |= EnvCellFlags.HasStaticObjs;
                else
                    envCell.Flags &= ~EnvCellFlags.HasStaticObjs;

                if (dc.RestrictionObj != 0)
                    envCell.Flags |= EnvCellFlags.HasRestrictionObj;
                else
                    envCell.Flags &= ~EnvCellFlags.HasRestrictionObj;

                result.Add(envCell);
            }

            return result;
        }

        /// <summary>
        /// Copy all cells from another dungeon document into this one.
        /// Renumbers cells sequentially starting from <paramref name="startCellNum"/>,
        /// remaps portal and visible-cell references, and preserves all other data.
        /// </summary>
        /// <param name="startCellNum">First cell number to use. Pass 0x0100 for empty landblocks,
        /// or a higher value to avoid overwriting existing building cells.</param>
        public void CopyFrom(DungeonDocument source, ushort startCellNum = 0x0100) {
            Cells.Clear();
            _nextCellNumber = startCellNum;
            _recycledCellNumbers.Clear();

            var cellMap = new Dictionary<ushort, ushort>();
            ushort nextNum = startCellNum;
            foreach (var src in source.Cells) {
                cellMap[src.CellNumber] = nextNum++;
            }

            foreach (var src in source.Cells) {
                ushort newCellNum = cellMap[src.CellNumber];
                var dc = new DungeonCellData {
                    CellNumber = newCellNum,
                    EnvironmentId = src.EnvironmentId,
                    CellStructure = src.CellStructure,
                    Origin = src.Origin,
                    Orientation = src.Orientation,
                    Flags = src.Flags,
                    RestrictionObj = src.RestrictionObj,
                };
                dc.Surfaces.AddRange(src.Surfaces);
                foreach (var cp in src.CellPortals) {
                    ushort remappedOther = cp.OtherCellId;
                    if (cellMap.TryGetValue(cp.OtherCellId, out var mapped))
                        remappedOther = mapped;
                    dc.CellPortals.Add(new DungeonCellPortalData {
                        OtherCellId = remappedOther,
                        PolygonId = cp.PolygonId,
                        OtherPortalId = cp.OtherPortalId,
                        Flags = cp.Flags
                    });
                }
                foreach (var vc in src.VisibleCells) {
                    dc.VisibleCells.Add(cellMap.TryGetValue(vc, out var mappedVc) ? mappedVc : vc);
                }
                foreach (var stab in src.StaticObjects) {
                    dc.StaticObjects.Add(new DungeonStabData {
                        Id = stab.Id,
                        Origin = stab.Origin,
                        Orientation = stab.Orientation
                    });
                }
                Cells.Add(dc);
            }
            _nextCellNumber = nextNum;
            MarkDirty();
        }

        public enum ValidationSeverity { Info, Warning, Error }

        public record ValidationResult(ValidationSeverity Severity, string Message, ushort? CellNumber = null) {
            public string Icon => Severity switch {
                ValidationSeverity.Error => "[ERROR]",
                ValidationSeverity.Warning => "[WARN]",
                _ => "[INFO]"
            };
        }

        public List<string> Validate() {
            return ValidateComprehensive()
                .Where(r => r.Severity != ValidationSeverity.Info)
                .Select(r => r.Message)
                .ToList();
        }

        public List<ValidationResult> ValidateComprehensive() {
            var results = new List<ValidationResult>();
            if (Cells.Count == 0) {
                results.Add(new(ValidationSeverity.Warning, "Dungeon has no cells."));
                return results;
            }

            var cellNums = new HashSet<ushort>(Cells.Select(c => c.CellNumber));

            // Duplicate cell numbers
            var dupes = Cells.GroupBy(c => c.CellNumber).Where(g => g.Count() > 1);
            foreach (var dupe in dupes)
                results.Add(new(ValidationSeverity.Error,
                    $"Duplicate cell number 0x{dupe.Key:X4} ({dupe.Count()} cells)", dupe.Key));

            // Orphaned portal references
            foreach (var cell in Cells) {
                foreach (var portal in cell.CellPortals) {
                    if (portal.OtherCellId != 0 && portal.OtherCellId != 0xFFFF &&
                        !cellNums.Contains(portal.OtherCellId)) {
                        results.Add(new(ValidationSeverity.Error,
                            $"Cell 0x{cell.CellNumber:X4} portal references non-existent cell 0x{portal.OtherCellId:X4}",
                            cell.CellNumber));
                    }
                }
            }

            // One-way portals (A→B exists but B→A doesn't)
            foreach (var cell in Cells) {
                foreach (var portal in cell.CellPortals) {
                    var other = GetCell(portal.OtherCellId);
                    if (other != null && !other.CellPortals.Any(p => p.OtherCellId == cell.CellNumber)) {
                        results.Add(new(ValidationSeverity.Warning,
                            $"One-way portal: 0x{cell.CellNumber:X4} → 0x{portal.OtherCellId:X4} (no return link)",
                            cell.CellNumber));
                    }
                }
            }

            // Disconnected cells (unreachable from first cell)
            var visited = new HashSet<ushort>();
            var queue = new Queue<ushort>();
            var firstCell = Cells[0].CellNumber;
            queue.Enqueue(firstCell);
            visited.Add(firstCell);
            while (queue.Count > 0) {
                var current = queue.Dequeue();
                var cell = GetCell(current);
                if (cell == null) continue;
                foreach (var portal in cell.CellPortals) {
                    if (cellNums.Contains(portal.OtherCellId) && visited.Add(portal.OtherCellId))
                        queue.Enqueue(portal.OtherCellId);
                }
            }
            foreach (var cell in Cells) {
                if (!visited.Contains(cell.CellNumber))
                    results.Add(new(ValidationSeverity.Warning,
                        $"Cell 0x{cell.CellNumber:X4} is disconnected from the main dungeon (unreachable from 0x{firstCell:X4})",
                        cell.CellNumber));
            }

            // Missing surfaces
            foreach (var cell in Cells) {
                if (cell.Surfaces.Count == 0)
                    results.Add(new(ValidationSeverity.Warning,
                        $"Cell 0x{cell.CellNumber:X4} has no surfaces assigned (will be invisible in-game)",
                        cell.CellNumber));
            }

            // Empty VisibleCells
            int emptyStabs = Cells.Count(c => c.VisibleCells.Count == 0);
            if (emptyStabs > 0)
                results.Add(new(ValidationSeverity.Info,
                    $"{emptyStabs} cell(s) have empty VisibleCells — use Compute Visibility before export"));

            // Summary
            if (results.Count == 0)
                results.Add(new(ValidationSeverity.Info,
                    $"All {Cells.Count} cells passed validation."));

            return results;
        }

        /// <summary>
        /// Auto-fixes common issues: adds missing back-portals for one-way connections.
        /// Returns the number of fixes applied.
        /// </summary>
        public int AutoFixPortals() {
            int fixes = 0;
            foreach (var cell in Cells) {
                foreach (var portal in cell.CellPortals) {
                    var other = GetCell(portal.OtherCellId);
                    if (other != null && !other.CellPortals.Any(p => p.OtherCellId == cell.CellNumber)) {
                        other.CellPortals.Add(new DungeonCellPortalData {
                            OtherCellId = cell.CellNumber,
                            PolygonId = portal.OtherPortalId,
                            OtherPortalId = portal.PolygonId,
                            Flags = portal.Flags
                        });
                        fixes++;
                    }
                }
            }
            if (fixes > 0) MarkDirty();
            return fixes;
        }

        /// <summary>
        /// Computes VisibleCells (stab lists) for all cells via BFS through portal connections.
        /// The AC client uses these to know which cells to prefetch and keep loaded.
        /// Each cell's visible set includes itself plus all cells reachable within maxDepth portal hops.
        /// </summary>
        public int ComputeVisibleCells(int maxDepth = 3) {
            var adjacency = new Dictionary<ushort, List<ushort>>();
            foreach (var cell in Cells) {
                adjacency[cell.CellNumber] = cell.CellPortals
                    .Select(p => p.OtherCellId)
                    .Where(id => id != 0 && id != 0xFFFF && Cells.Any(c => c.CellNumber == id))
                    .ToList();
            }

            int totalUpdated = 0;
            foreach (var cell in Cells) {
                var visible = new HashSet<ushort> { cell.CellNumber };
                var frontier = new Queue<(ushort id, int depth)>();
                frontier.Enqueue((cell.CellNumber, 0));

                while (frontier.Count > 0) {
                    var (current, depth) = frontier.Dequeue();
                    if (depth >= maxDepth) continue;
                    if (!adjacency.TryGetValue(current, out var neighbors)) continue;

                    foreach (var neighbor in neighbors) {
                        if (visible.Add(neighbor)) {
                            frontier.Enqueue((neighbor, depth + 1));
                        }
                    }
                }

                var sorted = visible.OrderBy(x => x).ToList();
                if (!cell.VisibleCells.SequenceEqual(sorted)) {
                    cell.VisibleCells.Clear();
                    cell.VisibleCells.AddRange(sorted);
                    totalUpdated++;
                }
            }

            if (totalUpdated > 0) MarkDirty();
            return totalUpdated;
        }

        /// <summary>
        /// Recomputes PortalFlags for all cell portals from CellStruct geometry.
        /// The PortalSide flag (bit 1) indicates which half-space of the portal
        /// polygon plane is the cell interior. This must be correct for the AC client
        /// to traverse portals properly.
        /// </summary>
        public int RecomputePortalFlags(IDatReaderWriter dats) {
            int updated = 0;
            foreach (var cell in Cells) {
                uint envFileId = (uint)(cell.EnvironmentId | 0x0D000000);
                if (!dats.TryGet<DatReaderWriter.DBObjs.Environment>(envFileId, out var env)) continue;
                if (!env.Cells.TryGetValue(cell.CellStructure, out var cs)) continue;

                var centroid = Vector3.Zero;
                int vtxCount = cs.VertexArray.Vertices.Count;
                foreach (var vtx in cs.VertexArray.Vertices.Values)
                    centroid += vtx.Origin;
                if (vtxCount > 0) centroid /= vtxCount;

                foreach (var portal in cell.CellPortals) {
                    if (!cs.Polygons.TryGetValue(portal.PolygonId, out var poly)) continue;
                    if (poly.VertexIds.Count < 3) continue;

                    if (!cs.VertexArray.Vertices.TryGetValue((ushort)poly.VertexIds[0], out var v0)) continue;
                    if (!cs.VertexArray.Vertices.TryGetValue((ushort)poly.VertexIds[1], out var v1)) continue;
                    if (!cs.VertexArray.Vertices.TryGetValue((ushort)poly.VertexIds[2], out var v2)) continue;

                    var normal = Vector3.Normalize(Vector3.Cross(v1.Origin - v0.Origin, v2.Origin - v0.Origin));
                    float d = -Vector3.Dot(normal, v0.Origin);
                    float centroidDot = Vector3.Dot(normal, centroid) + d;

                    // AC client: PortalSide flag (0x0002) SET → portal_side=0 (interior on positive side)
                    //            PortalSide flag CLEAR        → portal_side=1 (interior on negative side)
                    ushort newFlags = (ushort)(portal.Flags & ~0x0002);
                    if (centroidDot >= 0)
                        newFlags |= 0x0002;

                    if (portal.Flags != newFlags) {
                        portal.Flags = newFlags;
                        updated++;
                    }
                }
            }
            if (updated > 0) MarkDirty();
            return updated;
        }

        public DungeonCellData? GetCell(ushort cellNumber) =>
            Cells.FirstOrDefault(c => c.CellNumber == cellNumber);
    }
}
