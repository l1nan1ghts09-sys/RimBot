using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>
    /// Строительство дома: стены и дверь ставятся чертежами, крыша — ванильной
    /// зоной «строить крышу», кровати — после появления крыши. Размер, материал
    /// и очерёдность выбираются по населению, запасам и текущей задаче.
    /// </summary>
    public static class Housing
    {
        private class Plan
        {
            public int side;
            public ThingDef stuff;
            public string name = "";
            public string why = "";
            public int wallCells;
        }

        private static int next;
        private static string line = "Дом: оцениваю, что строить.";
        private static int lastBuild = -99999;

        public static void ResetRuntime()
        {
            next = 0;
            lastBuild = -99999;
            line = "Дом: оцениваю, что строить.";
        }

        public static string Line(Map map) { return line; }

        public static void Run(Map map, int tick)
        {
            if (map == null) return;
            TradeSettings s = TradeEmpireMod.S;
            if (s == null || !s.autoHousing) return;
            if (tick < next) return;
            next = tick + 9000;
            try { Build(map, false); }
            catch (Exception e) { T.Warn("Дом: " + e.Message); }
        }

        /// <summary>Один проход строительства дома. Возвращает true, если что-то изменилось.</summary>
        public static bool Build(Map map, bool vanilla)
        {
            TradeSettings s = TradeEmpireMod.S;
            if (map == null || s == null || !s.autoHousing) return false;

            int pop = Math.Max(1, map.mapPawns.FreeColonistsSpawnedCount);
            int beds = 0, roofedBeds = 0;
            foreach (Building_Bed b in map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>())
            {
                if (b == null) continue;
                beds++;
                if (b.Position.Roofed(map)) roofedBeds++;
            }

            Site.Info site = Site.Best(map);
            bool naturalShelter = site != null && (site.cave || site.hollow);
            bool pocket = site != null && site.pocket;
            bool urgent = roofedBeds < Math.Min(pop, 2) || beds == 0;
            if (!urgent && Focus.Active && !Focus.Allows("Shelter"))
            {
                line = "Дом: пока не главная задача, жду фокус на жилье или нехватку кроватей.";
                return false;
            }
            if (GenHostility.AnyHostileActiveThreatToPlayer(map))
            {
                line = "Дом: сейчас бой — новую стройку не начинаю, сначала оборона.";
                return false;
            }

            // 1) Если комната уже замкнута, но осталась без крыши — просим крышу
            //    у колонистов (BuildRoof), а не ставим её телепортом.
            int requested = RequestMissingRoof(map);
            if (requested > 0)
            {
                line = "Дом: стены готовы, попросил колонистов накрыть " + requested + " кл. крышей.";
                Mind.Think("дом", line);
                return true;
            }

            // 2) В пещере/углублении естественная крыша уже есть: не тратим
            //    блоки на коробку, а просто ставим кровать под горной крышей.
            if (naturalShelter && EnsureBed(map, ref roofedBeds))
            {
                line = "Дом: использую пещеру как готовое укрытие — поставил кровать под горной крышей.";
                Mind.Think("дом", line);
                return true;
            }

            // 3) Если дом уже накрыт, но кроватей не хватает — заселяем его.
            if (roofedBeds < pop && EnsureBed(map, ref roofedBeds))
            {
                line = "Дом: поставил кровать в готовом крытом помещении (" + roofedBeds + "/" + pop + ").";
                Mind.Think("дом", line);
                return true;
            }

            // 4) Кроватей достаточно — дом есть; дальше только улучшения по фокусу.
            if (roofedBeds >= pop)
            {
                line = "Дом: крытых кроватей " + roofedBeds + "/" + pop + " — жильё обеспечено.";
                return false;
            }

            // 5) Уже есть заложенная дверь дома — не плодим второй чертёж.
            //    Стены обороны дверьми не считаются, иначе дом ждал бы вечно.
            if (PendingHouseBlueprints(map) >= 1)
            {
                line = "Дом: стены уже заложены чертежами, жду строителей (готово " + roofedBeds + "/" + pop + ").";
                return false;
            }

            // 6) Выбираем осмысленный шаблон под ресурсы и задачу.
            bool underPressure = Utility.ThreatPressure(map) >= 0.55f;
            Plan plan = ChoosePlan(map, pop, site, urgent, pocket, underPressure);
            if (plan == null)
            {
                line = "Дом: не хватает дерева/блоков на коробку — коплю материалы или строю пещеру.";
                return false;
            }

            if (TryBuildRoom(map, plan))
            {
                lastBuild = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
                line = "Дом: " + plan.name + " — " + plan.why + ". Поставил чертежи, крышу попрошу после стен.";
                Mind.Think("дом", line);
                return true;
            }

            line = "Дом: подходящее место не найдено — " + plan.name + " не влез, ищу площадку.";
            return false;
        }

        // ------------------------------------------------------------ шаблоны

        private static Plan ChoosePlan(Map map, int pop, Site.Info site, bool urgent, bool pocket, bool underPressure)
        {
            int wood = T.Count(map, ThingDefOf.WoodLog);
            int stone = 0;
            foreach (string n in new[] { "BlocksGranite", "BlocksLimestone", "BlocksSlate", "BlocksSandstone", "BlocksMarble" })
            {
                ThingDef d = T.Def(n);
                if (d != null) stone += T.Count(map, d);
            }

            // Размер: маленький стартовый домик, если людей мало или материалов в обрез;
            // иначе — по населению, но без гигантомании на бедной колонии.
            int side;
            if (underPressure) side = 5;                 // под угрозой — быстрый маленький дом
            else if (urgent && wood + stone < 260) side = 5;
            else if (pop <= 2 || wood + stone < 260) side = 5;
            else if (pop <= 4 || wood + stone < 520) side = 7;
            else side = 9;

            int wallCells = side * 4 - 4;
            ThingDef stuff = ChooseStuff(map, wallCells, stone, wood, urgent, out string whyStuff);
            if (stuff == null) return null;

            // Материал и размер — это и есть шаблон: дешёвый деревянный стартер,
            // каменный дом для долгой обороны или расширение под большое население.
            string kind;
            if (stuff == ThingDefOf.WoodLog) kind = "деревянный";
            else kind = "каменный";
            string size = side <= 5 ? "стартовый" : side <= 7 ? "средний" : "большой";

            string why;
            if (!Resources.CanAfford(map, ThingDefOf.Wall, stuff, wallCells + 1, urgent ? 0.9f : 0.65f, out why))
                return null;

            return new Plan
            {
                side = side,
                stuff = stuff,
                wallCells = wallCells,
                name = size + " " + kind + " дом " + side + "×" + side,
                why = "запас " + (stuff == ThingDefOf.WoodLog ? "дерева " + wood : "камня " + stone)
                    + ", людей " + pop + (urgent ? ", жильё срочно" : "")
                    + (underPressure ? ", под угрозой — компактно" : "") + "; " + whyStuff
                    + (Focus.Active ? ", задача: " + Focus.Title : "")
            };
        }

        private static ThingDef ChooseStuff(Map map, int wallCells, int stone, int wood, bool urgent, out string why)
        {
            why = "";
            // Камень прочнее и не горит — берём его, если его реально хватает.
            string[] stoneNames = { "BlocksGranite", "BlocksLimestone", "BlocksSlate", "BlocksSandstone", "BlocksMarble" };
            ThingDef bestStone = null;
            int bestCount = 0;
            foreach (string n in stoneNames)
            {
                ThingDef d = T.Def(n);
                if (d == null) continue;
                int c = T.Count(map, d);
                if (c > bestCount) { bestCount = c; bestStone = d; }
            }
            string whyStone;
            if (bestStone != null && Resources.CanAfford(map, ThingDefOf.Wall, bestStone, wallCells + 1,
                urgent ? 0.9f : 0.6f, out whyStone))
            {
                why = "камень " + bestCount + " — прочнее и не горит";
                return bestStone;
            }

            string whyWood;
            if (Resources.CanAfford(map, ThingDefOf.Wall, ThingDefOf.WoodLog, wallCells + 1,
                urgent ? 0.9f : 0.65f, out whyWood))
            {
                why = "дерево " + wood + " — быстро и дешево";
                return ThingDefOf.WoodLog;
            }
            why = "нет подходящего запаса";
            return null;
        }

        // ------------------------------------------------------------ стройка

        private static bool TryBuildRoom(Map map, Plan plan)
        {
            IntVec3 anchor = Infrastructure.Anchor(map);
            for (int r = 0; r <= 18; r += 3)
            {
                foreach (IntVec3 center in GenRadial.RadialCellsAround(anchor, r, true))
                {
                    if (!center.InBounds(map) || center.Fogged(map)) continue;
                    IntVec3 doorCell;
                    List<IntVec3> walls;
                    if (!CheckRoom(map, center, plan.side, plan.stuff, out walls, out doorCell)) continue;

                    foreach (IntVec3 c in walls)
                        GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Wall, c, map, Rot4.North, Faction.OfPlayer, plan.stuff);
                    GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Door, doorCell, map, Rot4.South, Faction.OfPlayer, plan.stuff);
                    return true;
                }
            }
            return false;
        }

        private static bool CheckRoom(Map map, IntVec3 center, int side, ThingDef stuff, out List<IntVec3> walls, out IntVec3 doorCell)
        {
            walls = new List<IntVec3>();
            doorCell = IntVec3.Invalid;
            int half = side / 2;
            IntVec3 min = new IntVec3(center.x - half, 0, center.z - half);
            ThingDef wall = ThingDefOf.Wall;
            ThingDef door = ThingDefOf.Door;
            if (wall == null || door == null) return false;

            for (int x = 0; x < side; x++)
            {
                for (int z = 0; z < side; z++)
                {
                    IntVec3 c = new IntVec3(min.x + x, 0, min.z + z);
                    if (!c.InBounds(map) || c.Fogged(map)) return false;
                    bool edge = x == 0 || z == 0 || x == side - 1 || z == side - 1;
                    if (!Site.GoodGround(map, c)) return false;
                    if (c.GetEdifice(map) != null) return false;
                    if (map.zoneManager.ZoneAt(c) != null) return false;
                    foreach (Thing t in map.thingGrid.ThingsAt(c))
                        if (t != null && t.def != null && !t.def.CanOverlapZones) return false;

                    if (!edge) continue;
                    if (x == side / 2 && z == 0) doorCell = c;
                    else walls.Add(c);
                }
            }
            if (walls.Count < 8 || !doorCell.IsValid) return false;

            // Проверяем весь контур до первого чертежа, иначе останутся рваные стены.
            foreach (IntVec3 c in walls)
            {
                var acc = GenConstruct.CanPlaceBlueprintAt(wall, c, Rot4.North, map, false, null, null, stuff);
                if (!acc.Accepted) return false;
            }
            var doorAcc = GenConstruct.CanPlaceBlueprintAt(door, doorCell, Rot4.South, map, false, null, null, stuff);
            return doorAcc.Accepted;
        }

        private static int PendingHouseBlueprints(Map map)
        {
            int doors = 0, walls = 0;
            try
            {
                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint))
                {
                    if (t == null || t.Faction != Faction.OfPlayer) continue;
                    ThingDef bd = t.def != null ? t.def.entityDefToBuild as ThingDef : null;
                    if (bd == ThingDefOf.Door) doors++;
                    else if (bd == ThingDefOf.Wall) walls++;
                }
                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame))
                {
                    if (t == null || t.Faction != Faction.OfPlayer) continue;
                    ThingDef bd = t.def != null ? t.def.entityDefToBuild as ThingDef : null;
                    if (bd == ThingDefOf.Door) doors++;
                    else if (bd == ThingDefOf.Wall) walls++;
                }
            }
            catch { }
            // Дверь — точный признак дома. Стены обороны сами по себе домом не
            // считаются, поэтому порог 8 отсекает только большой контур.
            return doors > 0 || walls >= 8 ? 1 : 0;
        }

        private static int RequestMissingRoof(Map map)
        {
            int requested = 0;
            try
            {
                foreach (Building_Bed b in map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>())
                {
                    if (b != null && !b.Position.Roofed(map))
                    {
                        requested += Roofs.RoofEnclosed(map, b.Position, 60);
                        if (requested > 0) break;
                    }
                }
                if (requested > 0) return requested;

                IntVec3 anchor = Infrastructure.Anchor(map);
                for (int r = 0; r <= 22 && requested == 0; r += 2)
                {
                    foreach (IntVec3 c in GenRadial.RadialCellsAround(anchor, r, true))
                    {
                        if (!c.InBounds(map) || c.Roofed(map)) continue;
                        requested = Roofs.RoofEnclosed(map, c, 60);
                        if (requested > 0) break;
                    }
                }
            }
            catch { }
            return requested;
        }

        private static bool EnsureBed(Map map, ref int roofedBeds)
        {
            int pop = Math.Max(1, map.mapPawns.FreeColonistsSpawnedCount);
            if (roofedBeds >= pop) return false;
            if (PendingBeds(map) >= 2) return false;
            ThingDef def = T.Def("Bed") ?? T.Def("DoubleBed");
            if (def == null || !Infrastructure.Available(def)) return false;
            if (Infrastructure.PlaceNear(map, def, Infrastructure.Anchor(map), 2, 20, true))
            {
                roofedBeds++;
                return true;
            }
            return false;
        }

        private static int PendingBeds(Map map)
        {
            int n = 0;
            try
            {
                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint))
                {
                    if (t == null || t.Faction != Faction.OfPlayer) continue;
                    ThingDef bd = t.def != null ? t.def.entityDefToBuild as ThingDef : null;
                    if (bd != null && bd.thingClass != null && typeof(Building_Bed).IsAssignableFrom(bd.thingClass)) n++;
                }
                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame))
                {
                    if (t == null || t.Faction != Faction.OfPlayer) continue;
                    ThingDef bd = t.def != null ? t.def.entityDefToBuild as ThingDef : null;
                    if (bd != null && bd.thingClass != null && typeof(Building_Bed).IsAssignableFrom(bd.thingClass)) n++;
                }
            }
            catch { }
            return n;
        }
    }
}
