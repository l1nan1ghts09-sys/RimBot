using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>
    /// Убирает устаревшие и пустые зоны, которые бот или игрок поставили раньше:
    /// дубли продовольственного/вещевого склада, пустые наружные свалки после
    /// появления крытого склада. Не трогает нужные фермерские зоны.
    /// </summary>
    public static class Zones
    {
        private static int next;
        private static int removedTotal;
        private static string line = "Зоны: проверяю устаревшие склады.";

        public static void ResetRuntime() { next = 0; removedTotal = 0; line = "Зоны: проверяю устаревшие склады."; }
        public static string Line(Map map) { return line; }
        public static int Removed { get { return removedTotal; } }

        public static void Run(Map map, int tick)
        {
            TradeSettings s = TradeEmpireMod.S;
            if (s == null || !s.autoZoneCleanup) return;
            if (map == null || tick < next) return;
            next = tick + 12000;
            int removed = Cleanup(map);
            removedTotal += removed;
            line = BuildLine(map, removed);
        }

        /// <summary>Публичный проход для навыка/других модулей. Возвращает число удалённых зон.</summary>
        public static int Cleanup(Map map)
        {
            if (map == null) return 0;
            if (TradeEmpireMod.S != null && !TradeEmpireMod.S.autoZoneCleanup) return 0;
            int removed = 0;
            try
            {
                List<Zone_Stockpile> piles = map.zoneManager.AllZones.OfType<Zone_Stockpile>().ToList();
                bool hasRoofedPile = piles.Any(z => z.cells.Count >= 4 && z.cells.All(c => c.Roofed(map))
                    && z.AllContainedThings.Any());

                // 1) Совсем пустые/битые зоны бота.
                foreach (Zone z in map.zoneManager.AllZones.ToList())
                {
                    if (z == null) continue;
                    if (z.cells.Count == 0)
                    {
                        Delete(z);
                        removed++;
                        continue;
                    }
                    if (!IsBotZone(z)) continue;
                    if (z.cells.Count == 0) continue;

                    // 2) Дубли одной роли: оставляем лучшую (крытую и большую).
                    string role = Role(z);
                    if (role == null) continue;
                    Zone_Stockpile best = piles
                        .Where(p => Role(p) == role)
                        .OrderByDescending(p => RoofScore(map, p))
                        .ThenByDescending(p => p.cells.Count)
                        .ThenByDescending(p => p.AllContainedThings.Any() ? 1 : 0)
                        .FirstOrDefault();
                    if (best != null && best != z)
                    {
                        Zone_Stockpile zs = z as Zone_Stockpile;
                        bool empty = zs == null || !zs.AllContainedThings.Any();
                        bool unroofed = z.cells.Any(c => !c.Roofed(map));
                        if (empty || (unroofed && RoofScore(map, best) > RoofScore(map, (Zone_Stockpile)z)))
                        {
                            Delete(z);
                            removed++;
                        }
                    }
                }

                // 3) Пустые наружные свалки, когда уже есть крытый склад. Игрок может
                //    ставить их сам — после крыши они только путают hauling.
                if (hasRoofedPile)
                {
                    foreach (Zone_Stockpile z in map.zoneManager.AllZones.OfType<Zone_Stockpile>().ToList())
                    {
                        if (z == null || z.cells.Count == 0) continue;
                        if (z.cells.All(c => c.Roofed(map))) continue;
                        if (z.AllContainedThings.Any()) continue;
                        if (IsBotZone(z)) continue; // уже обработано выше
                        Delete(z);
                        removed++;
                    }
                }
            }
            catch (Exception e) { T.Warn("Зоны: " + e.Message); }
            if (removed > 0)
                Mind.Think("зоны", "Убрал устаревших зон: " + removed + ". Склады не дублируются, пустые наружные зоны не мешают hauling.");
            return removed;
        }

        public static bool HasObsolete(Map map)
        {
            if (map == null) return false;
            try
            {
                foreach (Zone z in map.zoneManager.AllZones)
                {
                    if (z == null || z.cells.Count == 0) return true;
                    if (!IsBotZone(z)) continue;
                    if (Role(z) == null) continue;
                    int same = map.zoneManager.AllZones.Count(o => Role(o) == Role(z));
                    if (same > 1) return true;
                }
            }
            catch { }
            return false;
        }

        private static bool IsBotZone(Zone z)
        {
            return z != null && z.label != null
                && (z.label.StartsWith("Еда (бот)") || z.label.StartsWith("Вещи (бот)"));
        }

        private static string Role(Zone z)
        {
            if (z == null || z.label == null) return null;
            if (z.label.StartsWith("Еда (бот)")) return "food";
            if (z.label.StartsWith("Вещи (бот)")) return "goods";
            return null;
        }

        private static int RoofScore(Map map, Zone_Stockpile z)
        {
            if (z == null || z.cells.Count == 0) return 0;
            return z.cells.All(c => c.Roofed(map)) ? 10 : 0;
        }

        private static void Delete(Zone z)
        {
            try { z.Delete(); } catch (Exception e) { T.Warn("Не удалось удалить зону: " + e.Message); }
        }

        private static string BuildLine(Map map, int justRemoved)
        {
            int all = 0, bot = 0, empty = 0, roofed = 0;
            try
            {
                foreach (Zone z in map.zoneManager.AllZones)
                {
                    all++;
                    if (IsBotZone(z)) bot++;
                    if (z.cells.Count == 0) empty++;
                    if (z.cells.Count > 0 && z.cells.All(c => c.Roofed(map))) roofed++;
                }
            }
            catch { }
            string text = "Зоны: всего " + all + " (бот " + bot + ", крытых " + roofed + ", пустых " + empty + ")";
            if (removedTotal > 0) text += ", убрано за сессию " + removedTotal;
            if (justRemoved > 0) text += ", только что " + justRemoved;
            return text + ".";
        }
    }
}
