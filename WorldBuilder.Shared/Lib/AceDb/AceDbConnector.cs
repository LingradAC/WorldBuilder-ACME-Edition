using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Models;

namespace WorldBuilder.Shared.Lib.AceDb {
    /// <summary>
    /// Thin wrapper around MySqlConnector for reading/writing the ACE
    /// ace_world.landblock_instance table.
    /// </summary>
    public class AceDbConnector : IDisposable {
        private readonly AceDbSettings _settings;

        public AceDbConnector(AceDbSettings settings) {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// Tests the MySQL connection. Returns null on success or the error message on failure.
        /// </summary>
        public async Task<string?> TestConnectionAsync(CancellationToken ct = default) {
            try {
                await using var conn = new MySqlConnection(_settings.ConnectionString);
                await conn.OpenAsync(ct);
                return null;
            }
            catch (Exception ex) {
                return ex.Message;
            }
        }

        /// <summary>
        /// Queries all outdoor landblock_instance rows for the given landblock IDs.
        /// Outdoor cells have cell numbers 0x0001–0x0040 (1–64).
        /// Uses a single bulk query for large sets, batched queries for smaller ones.
        /// Does not read rotation angles; use GetInstancesAsync for full placement data.
        /// </summary>
        public async Task<List<LandblockInstanceRecord>> GetOutdoorInstancesAsync(
            IEnumerable<ushort> landblockIds, CancellationToken ct = default) {

            var lbSet = new HashSet<ushort>(landblockIds);
            var results = new List<LandblockInstanceRecord>();
            await using var conn = new MySqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(ct);

            if (lbSet.Count > 500) {
                // For large sets, fetch all outdoor instances in one query and filter in memory
                const string sql = @"
                    SELECT `guid`, `weenie_Class_Id`, `obj_Cell_Id`,
                           `origin_X`, `origin_Y`, `origin_Z`
                    FROM `landblock_instance`
                    WHERE (`obj_Cell_Id` & 0xFFFF) BETWEEN 1 AND 64";

                await using var cmd = new MySqlCommand(sql, conn);
                cmd.CommandTimeout = 300;
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) {
                    uint objCellId = reader.GetUInt32("obj_Cell_Id");
                    ushort lbId = (ushort)(objCellId >> 16);
                    if (!lbSet.Contains(lbId)) continue;

                    results.Add(new LandblockInstanceRecord {
                        Guid = reader.GetUInt32("guid"),
                        WeenieClassId = reader.GetUInt32("weenie_Class_Id"),
                        ObjCellId = objCellId,
                        OriginX = reader.GetFloat("origin_X"),
                        OriginY = reader.GetFloat("origin_Y"),
                        OriginZ = reader.GetFloat("origin_Z"),
                    });
                }
            }
            else {
                // For small sets, query per landblock
                foreach (var lbId in lbSet) {
                    uint lbIdShifted = (uint)lbId << 16;
                    uint minCellId = lbIdShifted | 0x0001;
                    uint maxCellId = lbIdShifted | 0x0040;

                    const string sql = @"
                        SELECT `guid`, `weenie_Class_Id`, `obj_Cell_Id`,
                               `origin_X`, `origin_Y`, `origin_Z`
                        FROM `landblock_instance`
                        WHERE `obj_Cell_Id` >= @minCell AND `obj_Cell_Id` <= @maxCell";

                    await using var cmd = new MySqlCommand(sql, conn);
                    cmd.Parameters.AddWithValue("@minCell", minCellId);
                    cmd.Parameters.AddWithValue("@maxCell", maxCellId);

                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct)) {
                        results.Add(new LandblockInstanceRecord {
                            Guid = reader.GetUInt32("guid"),
                            WeenieClassId = reader.GetUInt32("weenie_Class_Id"),
                            ObjCellId = reader.GetUInt32("obj_Cell_Id"),
                            OriginX = reader.GetFloat("origin_X"),
                            OriginY = reader.GetFloat("origin_Y"),
                            OriginZ = reader.GetFloat("origin_Z"),
                        });
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Queries landblock_instance rows for a single landblock, optionally restricted by cell range.
        /// Use this to load all instances (outdoor + dungeon), or only dungeon cells (e.g. 0x0100–0xFFFE),
        /// so server operators can see and sync generators, items, and portals in dungeons.
        /// When includeAngles is true, reads angles_w/x/y/z for full placement round-trip.
        /// </summary>
        /// <param name="landblockId">Landblock ID (e.g. 0x01D9).</param>
        /// <param name="cellMin">Minimum cell number (inclusive). Null = 1 (outdoor start).</param>
        /// <param name="cellMax">Maximum cell number (inclusive). Null = 0xFFFE (all interiors).</param>
        /// <param name="includeAngles">When true, SELECT includes angles_w/x/y/z.</param>
        public async Task<List<LandblockInstanceRecord>> GetInstancesAsync(
            ushort landblockId,
            ushort? cellMin = null,
            ushort? cellMax = null,
            bool includeAngles = true,
            CancellationToken ct = default) {

            ushort cMin = cellMin ?? 1;
            ushort cMax = cellMax ?? 0xFFFE;
            uint lbIdShifted = (uint)landblockId << 16;
            uint minCellId = lbIdShifted | cMin;
            uint maxCellId = lbIdShifted | cMax;

            string cols = includeAngles
                ? "`guid`, `weenie_Class_Id`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`, `angles_w`, `angles_x`, `angles_y`, `angles_z`"
                : "`guid`, `weenie_Class_Id`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`";

            string sql = $@"
                SELECT {cols}
                FROM `landblock_instance`
                WHERE `obj_Cell_Id` >= @minCell AND `obj_Cell_Id` <= @maxCell";

            var results = new List<LandblockInstanceRecord>();
            await using var conn = new MySqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@minCell", minCellId);
            cmd.Parameters.AddWithValue("@maxCell", maxCellId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) {
                var rec = new LandblockInstanceRecord {
                    Guid = reader.GetUInt32("guid"),
                    WeenieClassId = reader.GetUInt32("weenie_Class_Id"),
                    ObjCellId = reader.GetUInt32("obj_Cell_Id"),
                    OriginX = reader.GetFloat("origin_X"),
                    OriginY = reader.GetFloat("origin_Y"),
                    OriginZ = reader.GetFloat("origin_Z"),
                };
                if (includeAngles && reader["angles_w"] != DBNull.Value) {
                    rec.AnglesW = reader.GetFloat("angles_w");
                    rec.AnglesX = reader.GetFloat("angles_x");
                    rec.AnglesY = reader.GetFloat("angles_y");
                    rec.AnglesZ = reader.GetFloat("angles_z");
                }
                results.Add(rec);
            }
            return results;
        }

        /// <summary>
        /// Generates a single INSERT statement for ace_world.landblock_instance.
        /// Use for placing generators/items/portals in dungeons. If Guid is 0, a new guid is generated.
        /// Angles default to identity quaternion (0, 0, 0, 1) when null.
        /// </summary>
        public static string GenerateInsertSql(LandblockInstanceRecord record, string databaseName = "ace_world") {
            uint guid = record.Guid;
            if (guid == 0)
                guid = (uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue);

            float w = record.AnglesW ?? 0f;
            float x = record.AnglesX ?? 0f;
            float y = record.AnglesY ?? 0f;
            float z = record.AnglesZ ?? 1f;
            if (record.AnglesW == null && record.AnglesX == null && record.AnglesY == null && record.AnglesZ == null) {
                w = 1f; // identity quaternion (W=1, X=Y=Z=0)
                x = 0f;
                y = 0f;
                z = 0f;
            }

            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "INSERT INTO `{0}`.`landblock_instance` (`guid`, `weenie_Class_Id`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`, `angles_w`, `angles_x`, `angles_y`, `angles_z`) VALUES ({1}, {2}, {3}, {4:F6}, {5:F6}, {6:F6}, {7:F6}, {8:F6}, {9:F6}, {10:F6});",
                databaseName, guid, record.WeenieClassId, record.ObjCellId,
                record.OriginX, record.OriginY, record.OriginZ,
                w, x, y, z);
        }

        /// <summary>
        /// Generates a batch of INSERT statements for landblock_instance (e.g. dungeon generator placements).
        /// </summary>
        public static string GenerateInsertSqlBatch(IEnumerable<LandblockInstanceRecord> records, string databaseName = "ace_world") {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("-- ACME WorldBuilder: landblock_instance (generators/items/portals)");
            sb.AppendLine($"-- Database: {databaseName}");
            sb.AppendLine();
            foreach (var r in records)
                sb.AppendLine(GenerateInsertSql(r, databaseName));
            return sb.ToString();
        }

        /// <summary>
        /// Converts dungeon instance placements to landblock_instance records for SQL generation.
        /// </summary>
        public static List<LandblockInstanceRecord> ToLandblockInstanceRecords(
            ushort landblockId,
            IEnumerable<DungeonInstancePlacement> placements) {
            var list = new List<LandblockInstanceRecord>();
            foreach (var p in placements) {
                uint objCellId = ((uint)landblockId << 16) | p.CellNumber;
                var q = p.Orientation;
                list.Add(new LandblockInstanceRecord {
                    Guid = 0,
                    WeenieClassId = p.WeenieClassId,
                    ObjCellId = objCellId,
                    OriginX = p.Origin.X,
                    OriginY = p.Origin.Y,
                    OriginZ = p.Origin.Z,
                    AnglesW = q.W,
                    AnglesX = q.X,
                    AnglesY = q.Y,
                    AnglesZ = q.Z,
                });
            }
            return list;
        }

        /// <summary>
        /// Converts outdoor instance placements to landblock_instance records for SQL generation.
        /// </summary>
        public static List<LandblockInstanceRecord> ToLandblockInstanceRecordsFromOutdoor(
            IEnumerable<OutdoorInstancePlacement> placements) {
            var list = new List<LandblockInstanceRecord>();
            foreach (var p in placements) {
                uint objCellId = ((uint)p.LandblockId << 16) | p.CellNumber;
                list.Add(new LandblockInstanceRecord {
                    Guid = 0,
                    WeenieClassId = p.WeenieClassId,
                    ObjCellId = objCellId,
                    OriginX = p.OriginX,
                    OriginY = p.OriginY,
                    OriginZ = p.OriginZ,
                    AnglesW = p.AnglesW,
                    AnglesX = p.AnglesX,
                    AnglesY = p.AnglesY,
                    AnglesZ = p.AnglesZ,
                });
            }
            return list;
        }

        /// <summary>
        /// Executes a batch of SQL statements (the generated reposition script) against the database.
        /// </summary>
        public async Task<int> ExecuteSqlAsync(string sql, CancellationToken ct = default) {
            await using var conn = new MySqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new MySqlCommand(sql, conn);
            return await cmd.ExecuteNonQueryAsync(ct);
        }

        /// <summary>
        /// Weenie name lookup result for pickers (ID, display name, and optional Setup DID for 3D preview).
        /// </summary>
        public record WeenieEntry(uint ClassId, string Name, uint SetupId);

        /// <summary>
        /// Loads weenie class IDs, names, and setup DIDs from ace_world for picker/list UI.
        /// Name comes from weenie_properties_string type 1 (PropertyString.Name).
        /// Setup DID comes from weenie_properties_d_i_d type 1 (PropertyDataId.Setup).
        /// </summary>
        /// <param name="search">Optional filter: names containing this text (case-insensitive). Supports partial matching.</param>
        /// <param name="limit">Max results (default 500).</param>
        public async Task<List<WeenieEntry>> GetWeenieNamesAsync(string? search = null, int limit = 500, CancellationToken ct = default) {
            var results = new List<WeenieEntry>();
            try {
                await using var conn = new MySqlConnection(_settings.ConnectionString);
                await conn.OpenAsync(ct);

                string sql;
                if (string.IsNullOrWhiteSpace(search)) {
                    sql = @"
                        SELECT n.`object_Id`, n.`value` AS `name`,
                               COALESCE(d.`value`, 0) AS `setup_did`
                        FROM `weenie_properties_string` n
                        LEFT JOIN `weenie_properties_d_i_d` d
                            ON d.`object_Id` = n.`object_Id` AND d.`type` = 1
                        WHERE n.`type` = 1
                        ORDER BY n.`value`
                        LIMIT @limit";
                }
                else {
                    sql = @"
                        SELECT n.`object_Id`, n.`value` AS `name`,
                               COALESCE(d.`value`, 0) AS `setup_did`
                        FROM `weenie_properties_string` n
                        LEFT JOIN `weenie_properties_d_i_d` d
                            ON d.`object_Id` = n.`object_Id` AND d.`type` = 1
                        WHERE n.`type` = 1
                          AND n.`value` LIKE CONCAT('%', @search, '%')
                        ORDER BY n.`value`
                        LIMIT @limit";
                }

                await using var cmd = new MySqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@limit", limit);
                if (!string.IsNullOrWhiteSpace(search))
                    cmd.Parameters.AddWithValue("@search", search.Trim());

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) {
                    results.Add(new WeenieEntry(
                        reader.GetUInt32("object_Id"),
                        reader.GetString("name"),
                        reader.IsDBNull(reader.GetOrdinal("setup_did")) ? 0 : reader.GetUInt32("setup_did")
                    ));
                }
            }
            catch (MySqlException) {
            }

            return results;
        }

        /// <summary>
        /// Batch lookup of Setup DIDs (PropertyDataId.Setup = type 1) for a set of weenie class IDs.
        /// Returns a dictionary mapping WCID -> Setup DID. WCIDs without a Setup are omitted.
        /// </summary>
        public async Task<Dictionary<uint, uint>> GetSetupDidsAsync(
            IEnumerable<uint> weenieClassIds, CancellationToken ct = default) {
            var result = new Dictionary<uint, uint>();
            var idList = new HashSet<uint>(weenieClassIds).ToList();
            if (idList.Count == 0) return result;

            try {
                await using var conn = new MySqlConnection(_settings.ConnectionString);
                await conn.OpenAsync(ct);

                for (int offset = 0; offset < idList.Count; offset += 500) {
                    var batch = idList.Skip(offset).Take(500).ToList();
                    var paramNames = string.Join(",", batch.Select((_, i) => $"@w{offset + i}"));
                    var sql = $@"SELECT `object_Id`, `value`
                                 FROM `weenie_properties_d_i_d`
                                 WHERE `type` = 1 AND `object_Id` IN ({paramNames})";

                    await using var cmd = new MySqlCommand(sql, conn);
                    for (int i = 0; i < batch.Count; i++)
                        cmd.Parameters.AddWithValue($"@w{offset + i}", batch[i]);

                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                        result.TryAdd(reader.GetUInt32("object_Id"), reader.GetUInt32("value"));
                }
            }
            catch (MySqlException) {
            }

            return result;
        }

        public void Dispose() { }
    }
}
