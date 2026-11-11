using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>
    /// Осмысленные шаблоны поведения, которые зависят от рельефа и бюджета:
    /// пещерная база с узким входом, пожарная безопасность у батарей,
    /// бережливая ранняя стройка и понятные объяснения в журнале.
    /// </summary>
    public static class Patterns
    {
        private static int next;
        private static bool caveRoomDone;
        private static string line = "Паттерны: оцениваю рельеф и раннюю экономику.";

        public static void ResetRuntime() { next = 0; caveRoomDone = false; line = "Паттерны: оцениваю рельеф и раннюю экономику."; }
        public static string Line(Map map) { return line; }

        public static void Run(Map map, int tick)
        {
            TradeSettings s = TradeEmpireMod.S;
            if (s == null || !s.autoPatterns) return;
            if (map == null || tick < next) return;
            next = tick + 9000;
            try
            {
                string cave = CavePattern(map);
                bool fire = FireSafety(map);
                line = cave + (fire ? " Поставил пожарную защиту у батарей." : "");
            }
            catch (Exception e) { T.Warn("Паттерны поведения: " + e.Message); }
        }

        /// <summary>Пещерная база: дверь в узком проходе и бережное использование скалы.</summary>
        public static string CavePattern(Map map)
        {
            if (TradeEmpireMod.S != null && !TradeEmpireMod.S.autoPatterns) return "Паттерны: выключены в настройках.";
            Site.Info site = Site.Best(map);
            if (site == null || (!site.cave && !site.hollow))
                return site != null && site.poor
                    ? "Открытая база: материалов мало, строю дешёвые укрытия и не трачусь на лишние стены."
                    : "Открытая база: места хватает, развиваю фермы и энергетику.";

            if (GenHostility.AnyHostileActiveThreatToPlayer(map))
                return "Пещера: сейчас бой, вход и укрытия важнее обустройства.";

            string text = "Пещера: естественные стены и горная крыша уже дают укрытие.";
            int changed = 0;
            if (!caveRoomDone && Beds(map) < Math.Max(1, map.mapPawns.FreeColonistsSpawnedCount)
                && TryCaveRoom(map, site))
            {
                changed++;
                text += " Вырубил комнату прямо в скале — не трачу блоки и дерево на стены.";
            }
            if (!HasDoorNear(map, Infrastructure.Anchor(map), 16) && TryChokeDoor(map, site))
            {
                changed++;
                text += " Поставил дверь в узком проходе — меньше открытых входов.";
            }
            if (site.hollow) text += " Это углубление в скале: дешевле открытой базы, пока нет материалов.";
            if (site.poor) text += " Бюджет ранний: сначала скала, потом расширение.";
            if (site.caveFloor < 18) text += " Внутри тесновато — расширю проход, когда появятся кирка и свет.";
            if (changed == 0) text += " Проверил вход и естественные стены.";
            return text;
        }

        public static bool FireSafety(Map map)
        {
            if (map == null) return false;
            if (TradeEmpireMod.S != null && !TradeEmpireMod.S.autoPatterns) return false;
            try
            {
                int batteries = map.listerBuildings.allBuildingsColonist.Count(b =>
                    b != null && b.def != null && b.def.defName != null
                    && b.def.defName.IndexOf("Battery", StringComparison.OrdinalIgnoreCase) >= 0);
                if (batteries == 0) return false;
                ThingDef popper = T.Def("FirefoamPopper");
                if (popper == null || !Infrastructure.Available(popper)) return false;
                if (Infrastructure.Exists(map, popper)) return false; // уже есть дом, чертёж или каркас

                Building battery = map.listerBuildings.allBuildingsColonist.FirstOrDefault(b =>
                    b != null && b.def != null && b.def.defName != null
                    && b.def.defName.IndexOf("Battery", StringComparison.OrdinalIgnoreCase) >= 0);
                if (battery == null) return false;
                if (!Utility.WeighOnly("firefoam", "поставить пенную ловушку у батарей", 0.45f, 0.08f, 0.02f, 1f,
                    "пожар от короткого замыкания не перекинется на базу",
                    "батареи и деревянная база могут сгореть от одной искры")) return false;

                if (Infrastructure.PlaceNear(map, popper, battery.Position, 1, 8, battery.Position.Roofed(map)))
                {
                    Mind.Think("пожар", "Поставил пенную ловушку у батарей: короткое замыкание не должно сжечь склад.");
                    return true;
                }
            }
            catch (Exception e) { T.Warn("Пожарная безопасность: " + e.Message); }
            return false;
        }

        private static int Beds(Map map)
        {
            try { return map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>().Count(); }
            catch { return 0; }
        }

        /// <summary>
        /// Дешёвое жильё в пещере: вырубает комнату 3×3 прямо в скале у базы,
        /// сохраняя горную крышу. Это заменяет постройку стен из блоков/дерева.
        /// </summary>
        private static bool TryCaveRoom(Map map, Site.Info site)
        {
            if (map == null || site == null) return false;
            try
            {
                int mine = 0;
                foreach (Designation d in map.designationManager.AllDesignations)
                    if (d.def == DesignationDefOf.Mine) mine++;
                if (mine >= 8) return false; // бот уже занят добычей

                IntVec3 anchor = Infrastructure.Anchor(map);
                for (int r = 4; r <= 14; r++)
                {
                    foreach (IntVec3 c in GenRadial.RadialCellsAround(anchor, r, true))
                    {
                        if (!c.InBounds(map) || c.Fogged(map) || !HasMountainRoof(map, c)) continue;
                        if (!Mineable(map, c)) continue;

                        List<IntVec3> block = new List<IntVec3>();
                        bool ok = true;
                        for (int dx = -1; dx <= 1 && ok; dx++)
                        {
                            for (int dz = -1; dz <= 1; dz++)
                            {
                                IntVec3 n = new IntVec3(c.x + dx, 0, c.z + dz);
                                if (!n.InBounds(map) || !Mineable(map, n) || !HasMountainRoof(map, n)) { ok = false; break; }
                                Thing rock = n.GetEdifice(map);
                                if (map.designationManager.DesignationOn(rock, DesignationDefOf.Mine) != null) { ok = false; break; }
                                block.Add(n);
                            }
                        }
                        if (!ok || block.Count < 9) continue;

                        int open = 0;
                        foreach (IntVec3 n in block)
                        {
                            foreach (IntVec3 a in GenAdj.CellsAdjacent8Way(new TargetInfo(n, map)))
                            {
                                if (!a.InBounds(map) || block.Contains(a)) continue;
                                if (a.Standable(map) && a.GetEdifice(map) == null) open++;
                            }
                        }
                        if (open < 2) continue;

                        foreach (IntVec3 n in block)
                        {
                            Thing rock = n.GetEdifice(map);
                            if (rock != null) map.designationManager.AddDesignation(new Designation(rock, DesignationDefOf.Mine));
                        }
                        caveRoomDone = true;
                        Mind.Think("пещера", "Вырубаю комнату 3×3 в скале у базы: горная крыша остаётся, стены уже есть, материалы не тратятся.");
                        return true;
                    }
                }
            }
            catch (Exception e) { T.Warn("Комната в скале: " + e.Message); }
            return false;
        }

        private static bool Mineable(Map map, IntVec3 c)
        {
            Thing ed = c.GetEdifice(map);
            return ed != null && ed.def != null && ed.def.mineable;
        }

        private static bool HasMountainRoof(Map map, IntVec3 c)
        {
            RoofDef rd = null;
            try { rd = map.roofGrid.RoofAt(c); } catch { }
            return rd != null && rd.defName != null
                && rd.defName.IndexOf("Rock", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool HasDoorNear(Map map, IntVec3 center, int radius)
        {
            try
            {
                return map.listerBuildings.allBuildingsColonist.Any(b =>
                    b != null && b.def == ThingDefOf.Door && b.Position.InHorDistOf(center, radius));
            }
            catch { return true; }
        }

        /// <summary>Ищет проход, где с двух сторон есть скала/стена, и ставит туда дверь.</summary>
        private static bool TryChokeDoor(Map map, Site.Info site)
        {
            try
            {
                ThingDef door = ThingDefOf.Door;
                if (door == null || !Infrastructure.Available(door)) return false;
                ThingDef stuff = door.MadeFromStuff ? GenStuff.DefaultStuffFor(door) : null;
                if (door.MadeFromStuff && stuff == null) return false;
                string why;
                if (!Resources.CanAfford(map, door, stuff, 1, 0.8f, out why)) return false;

                IntVec3 anchor = Infrastructure.Anchor(map);
                for (int r = 3; r <= 16; r++)
                {
                    foreach (IntVec3 c in GenRadial.RadialCellsAround(anchor, r, true))
                    {
                        if (!c.InBounds(map) || c.Fogged(map) || !Site.GoodGround(map, c)) continue;
                        if (c.GetEdifice(map) != null || map.zoneManager.ZoneAt(c) != null) continue;
                        if (!GenConstruct.CanPlaceBlueprintAt(door, c, Rot4.North, map, false, null, null, stuff).Accepted) continue;

                        int rockSides = 0;
                        foreach (IntVec3 n in GenAdj.CellsAdjacent8Way(new TargetInfo(c, map)))
                        {
                            if (!n.InBounds(map)) continue;
                            Thing ed = n.GetEdifice(map);
                            if (ed != null && ed.def != null && (ed.def.mineable || ed.def == ThingDefOf.Wall)) rockSides++;
                        }
                        if (rockSides < 2) continue;

                        GenConstruct.PlaceBlueprintForBuild(door, c, map, Rot4.North, Faction.OfPlayer, stuff);
                        Mind.Think("оборона", "Пещера: нашёл узкий проход у (" + c.x + ", " + c.z + ") — ставлю дверь, чтобы враг не обходил базу.");
                        return true;
                    }
                }
            }
            catch (Exception e) { T.Warn("Дверь в проходе: " + e.Message); }
            return false;
        }
    }
}
