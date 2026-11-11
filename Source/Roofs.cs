using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>
    /// Учит бота снимать крышу там, где она мешает: над солнечными панелями и
    /// открытыми грядками. Использует ванильную зону «без крыши», поэтому работу
    /// выполняют колонисты, а не телепортируется крыша.
    /// </summary>
    public static class Roofs
    {
        private static int next;
        private static string line = "Крыши: проверяю, где они мешают.";

        public static void ResetRuntime() { next = 0; line = "Крыши: проверяю, где они мешают."; }
        public static string Line(Map map) { return line; }

        public static void Run(Map map, int tick)
        {
            TradeSettings s = TradeEmpireMod.S;
            if (s == null || !s.autoRoofs) return;
            if (map == null || tick < next) return;
            next = tick + 9000;
            int marked = MarkProblemRoofs(map);
            line = BuildLine(map, marked);
        }

        public static bool NeedsWork(Map map)
        {
            if (map == null) return false;
            try
            {
                foreach (Building b in map.listerBuildings.allBuildingsColonist)
                {
                    if (b == null || b.def == null || b.def.defName == null) continue;
                    if (b.def.defName.IndexOf("Solar", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (b.OccupiedRect().Cells.Any(c => RemovableRoof(map, c))) return true;
                }
                foreach (Zone z in map.zoneManager.AllZones)
                {
                    Zone_Growing g = z as Zone_Growing;
                    if (g == null) continue;
                    foreach (IntVec3 c in g.cells)
                    {
                        TerrainDef t = c.GetTerrain(map);
                        if (t != null && t.defName != null
                            && t.defName.IndexOf("Hydroponic", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (RemovableRoof(map, c)) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        public static int MarkProblemRoofs(Map map)
        {
            if (map == null) return 0;
            int marked = 0;
            try
            {
                // 1) Солнечные панели должны видеть небо.
                foreach (Building b in map.listerBuildings.allBuildingsColonist)
                {
                    if (b == null || b.def == null || b.def.defName == null) continue;
                    if (b.def.defName.IndexOf("Solar", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    foreach (IntVec3 c in b.OccupiedRect().Cells)
                        if (MarkNoRoof(map, c)) marked++;
                }

                // 2) Открытые грядки под крышей не растут от солнца.
                foreach (Zone z in map.zoneManager.AllZones)
                {
                    Zone_Growing g = z as Zone_Growing;
                    if (g == null) continue;
                    foreach (IntVec3 c in g.cells)
                    {
                        TerrainDef t = c.GetTerrain(map);
                        if (t != null && t.defName != null
                            && t.defName.IndexOf("Hydroponic", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (MarkNoRoof(map, c)) marked++;
                    }
                }
            }
            catch (Exception e) { T.Warn("Крыши: " + e.Message); }
            if (marked > 0)
                Mind.Think("крыша", "Снимаю крышу с " + marked + " клеток: над солнечными панелями или грядкой она мешает свету. Работу выполнят колонисты.");
            return marked;
        }

        /// <summary>Помечает клетку как «без крыши», если там есть снимаемая крыша.</summary>
        private static bool RemovableRoof(Map map, IntVec3 c)
        {
            if (!c.InBounds(map)) return false;
            RoofDef rd = null;
            try { rd = map.roofGrid.RoofAt(c); } catch { }
            if (rd == null) return false;
            // Толстый горный навес снять нельзя; тонкая скальная крыша ванильным
            // инструментом «без крыши» снимается, поэтому её не запрещаем.
            if (rd.isThickRoof) return false;
            return true;
        }

        private static bool MarkNoRoof(Map map, IntVec3 c)
        {
            if (!RemovableRoof(map, c)) return false;
            try
            {
                if (map.areaManager.NoRoof[c]) return false;
                map.areaManager.NoRoof[c] = true;
                map.areaManager.BuildRoof[c] = false;
                return true;
            }
            catch { return false; }
        }

        private static string BuildLine(Map map, int justMarked)
        {
            int solar = 0, roofedSolar = 0, grow = 0, roofedGrow = 0, noRoof = 0;
            try
            {
                foreach (Building b in map.listerBuildings.allBuildingsColonist)
                {
                    if (b == null || b.def == null || b.def.defName == null) continue;
                    if (b.def.defName.IndexOf("Solar", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    solar++;
                    if (b.OccupiedRect().Cells.Any(c => c.Roofed(map))) roofedSolar++;
                }
                foreach (Zone z in map.zoneManager.AllZones)
                {
                    Zone_Growing g = z as Zone_Growing;
                    if (g == null) continue;
                    grow++;
                    if (g.cells.Any(c => c.Roofed(map))) roofedGrow++;
                }
                noRoof = map.areaManager.NoRoof.ActiveCells.Count();
            }
            catch { }
            string text = "Крыши: солнечных панелей " + solar + (roofedSolar > 0 ? " (под крышей " + roofedSolar + ")" : "")
                + ", грядок " + grow + (roofedGrow > 0 ? " (под крышей " + roofedGrow + ")" : "")
                + ", помечено «без крыши» " + noRoof + " кл.";
            if (justMarked > 0) text += " Только что пометил " + justMarked + ".";
            return text;
        }
    }
}
