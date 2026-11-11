using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>Строит то, без чего торговля не работает: консоль связи, маяк, недостающие прибыльные верстаки.</summary>
    public static class Infrastructure
    {
        private static int lastBench;

        public static void ResetRuntime() { lastBench = 0; }

        public static void Run(Map map)
        {
            if (PendingBlueprints(map) >= 3) return;
            // 1) Консоль связи
            ThingDef comms = T.Def("CommsConsole");
            if (comms != null && !Exists(map, comms) && Available(comms) && PlaceNear(map, comms, Anchor(map), 4, 28, true)) return;
            // 2) Маяк — рядом со складом
            ThingDef beacon = T.Def("OrbitalTradeBeacon");
            if (beacon != null && !Exists(map, beacon) && Available(beacon))
            {
                if (PlaceNear(map, beacon, StockpileCenter(map), 2, 12, false)) return;
            }
            // 3) Верстак с лучшей прибылью, которого ещё нет
            int tick = Find.TickManager.TicksGame;
            if (tick - lastBench < 30000) return;
            var colonists = map.mapPawns.FreeColonistsSpawned.ToList();
            ThingDef bestDef = null; float best = 0f;
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefs)
            {
                if (d.category != ThingCategory.Building || d.thingClass == null || !typeof(Building_WorkTable).IsAssignableFrom(d.thingClass)) continue;
                if (d.AllRecipes == null || d.AllRecipes.Count == 0 || !Available(d) || Exists(map, d)) continue;
                if (d.designationCategory == null) continue;
                var top = Planner.Rank(d, map, colonists).FirstOrDefault();
                if (top != null && top.feasible && top.density > best) { best = top.density; bestDef = d; }
            }
            if (bestDef != null && best > 0f)
            {
                lastBench = tick;
                if (PlaceNear(map, bestDef, Anchor(map), 3, 28, true))
                    T.Info("Строю верстак для прибыльного производства: " + bestDef.label);
            }
        }

        public static bool Available(ThingDef d)
        {
            if (d == null || d.blueprintDef == null) return false;   // спальное место и подобное ставится без чертежа
            if (d.researchPrerequisites != null && d.researchPrerequisites.Any(r => !r.IsFinished)) return false;
            return d.designationCategory != null;
        }

        public static bool Exists(Map map, ThingDef d)
        {
            if (map.listerBuildings.ColonistsHaveBuilding(d)) return true;
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint)) if (t.def.entityDefToBuild == d) return true;
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame)) if (t.def.entityDefToBuild == d) return true;
            return false;
        }

        private static int PendingBlueprints(Map map)
        {
            return map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count(t => t.Faction == Faction.OfPlayer);
        }

        public static IntVec3 Anchor(Map map)
        {
            var beds = map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>().ToList();
            if (beds.Count > 0) return new IntVec3((int)beds.Average(b => b.Position.x), 0, (int)beds.Average(b => b.Position.z));
            var cols = map.mapPawns.FreeColonistsSpawned.ToList();
            if (cols.Count > 0) return cols[0].Position;
            return map.Center;
        }

        private static IntVec3 StockpileCenter(Map map)
        {
            var zones = map.zoneManager.AllZones.OfType<Zone_Stockpile>().OrderByDescending(z => z.cells.Count).ToList();
            if (zones.Count == 0) return Anchor(map);
            var c = zones[0].cells;
            return new IntVec3((int)c.Average(x => x.x), 0, (int)c.Average(x => x.z));
        }

        public static bool PlaceNear(Map map, ThingDef def, IntVec3 center, int minR, int maxR, bool indoors)
        {
            if (def == null || def.blueprintDef == null) return false;   // без чертежа строить нельзя
            try { return PlaceNearInner(map, def, center, minR, maxR, indoors); }
            catch (Exception e) { T.Warn("Чертёж «" + (def != null ? def.label : "?") + "» не заложен: " + e.Message); return false; }
        }

        private static bool PlaceNearInner(Map map, ThingDef def, IntVec3 center, int minR, int maxR, bool indoors)
        {
            ThingDef stuff = def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null;
            bool needsPower = def.GetCompProperties<CompProperties_Power>() != null;
            for (int r = minR; r <= maxR; r++)
            {
                foreach (IntVec3 c in GenRadial.RadialCellsAround(center, r, true).Where(x => Math.Abs((x - center).LengthManhattan - r) <= 1))
                {
                    if (!c.InBounds(map) || !c.Standable(map) || c.Fogged(map)) continue;
                    if (indoors && !c.Roofed(map)) continue;
                    if (map.areaManager.Home != null && !map.areaManager.Home[c]) continue;
                    Rot4 rot = Rot4.South;
                    if (!GenConstruct.CanPlaceBlueprintAt(def, c, rot, map, false, null, null, stuff).Accepted) continue;
                    if (def.hasInteractionCell)
                    {
                        IntVec3 ic = ThingUtility.InteractionCellWhenAt(def, c, rot, map);
                        if (!ic.InBounds(map) || !ic.Standable(map)) continue;
                    }
                    int free = GenAdj.CellsAdjacent8Way(new TargetInfo(c, map)).Count(n => n.InBounds(map) && n.Standable(map));
                    if (free < 6) continue;
                    if (needsPower && !NearPower(map, c, 9)) continue;
                    GenConstruct.PlaceBlueprintForBuild(def, c, map, rot, Faction.OfPlayer, stuff);
                    T.Info("Заложен чертёж: " + def.label + " @ " + c);
                    return true;
                }
            }
            return false;
        }

        private static bool NearPower(Map map, IntVec3 c, int dist)
        {
            foreach (Building b in map.listerBuildings.allBuildingsColonist)
            {
                var p = b.GetComp<CompPower>();
                if (p != null && p.PowerNet != null && p.PowerNet.hasPowerSource && b.Position.InHorDistOf(c, dist)) return true;
            }
            return false;
        }
    }

    /// <summary>Подмешивает торговые технологии в приоритеты ResearchPass основного бота.</summary>
    public static class ResearchBoost
    {
        private static readonly string[] Trade =
        {
            "Electricity", "Smithing", "Tailoring", "Brewing", "DrugProduction", "Stonecutting", "ComplexClothing",
            "Machining", "MedicineProduction", "Hydroponics", "MicroelectronicsBasics", "Fabrication", "Microelectronics",
            "Greenhouse", "Pottery", "Jewel", "Trade", "Cooking", "Brew", "Cigar", "Tobacco", "Perfum", "Sculpt", "Furniture"
        };
        private static bool done;

        public static void InjectPriorities()
        {
            if (done) return;
            var t = T.FindType("RimWorldBot.ResearchPass");
            var f = t?.GetField("Priority", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (f == null) return;
            var old = f.GetValue(null) as string[];
            if (old == null) return;
            var merged = Trade.Concat(old.Where(x => !Trade.Contains(x))).ToArray();
            f.SetValue(null, merged);
            done = true;
            T.Info("Приоритеты исследований: добавлено торговых направлений — " + Trade.Length);
        }

        /// <summary>Новая игра — снова подмешиваем торговые направления (статический флаг сбрасывается).</summary>
        public static void Reset() { done = false; }
    }
}
