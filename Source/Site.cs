using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>
    /// Выбор места под базу с учётом рельефа: ровные клетки, плодородие, камень,
    /// вода/болото, близость к краю карты и уже существующие кровати.
    /// </summary>
    public static class Site
    {
        public class Info
        {
            public IntVec3 center = IntVec3.Invalid;
            public float score;
            public int buildable, rock, water, roofed;
            public int mountainRoof, naturalRock, caveFloor, perimeterRock, perimeterOpen;
            public float fertility;
            public bool cave, hollow, poor;
            public float wealth;
            public string kind = "открытое место";
            public string why = "";
            public string line = "";
        }

        private static Map cachedMap;
        private static Info cached;

        public static void ResetRuntime() { cachedMap = null; cached = null; }

        public static Info Best(Map map)
        {
            if (map == null) return new Info();
            if (cachedMap == map && cached != null) return cached;
            cachedMap = map;
            cached = Compute(map);
            return cached;
        }

        public static IntVec3 Center(Map map) { return Best(map).center; }
        public static string Line(Map map) { return Best(map).line; }

        /// <summary>Годная для стройки земля: не вода, не болото и не жижа.</summary>
        public static bool GoodGround(Map map, IntVec3 c)
        {
            if (map == null || !c.InBounds(map) || c.Fogged(map) || !c.Standable(map)) return false;
            TerrainDef t = c.GetTerrain(map);
            string n = t != null && t.defName != null ? t.defName : "";
            return n.IndexOf("Water", StringComparison.OrdinalIgnoreCase) < 0
                && n.IndexOf("Marsh", StringComparison.OrdinalIgnoreCase) < 0
                && n.IndexOf("Mud", StringComparison.OrdinalIgnoreCase) < 0;
        }


        private static Info Compute(Map map)
        {
            Info best = new Info();
            try
            {
                // Если база уже есть — не переносим её: считаем центром кровати.
                var beds = map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>().ToList();
                if (beds.Count > 0)
                {
                    best.center = new IntVec3((int)beds.Average(b => b.Position.x), 0, (int)beds.Average(b => b.Position.z));
                    best.score = 0.8f;
                    best.kind = "база уже заложена";
                    best.why = "база уже заложена: центр по кроватям";
                    best.line = "База: центр (" + best.center.x + ", " + best.center.z + ") — существующие кровати.";
                    return best;
                }

                IntVec3 focus = map.Center;
                var cols = map.mapPawns.FreeColonistsSpawned.ToList();
                bool aroundColonists = cols.Count > 0;
                if (aroundColonists)
                    focus = new IntVec3((int)cols.Average(p => p.Position.x), 0, (int)cols.Average(p => p.Position.z));

                int sizeX = map.Size.x, sizeZ = map.Size.z;
                int minX = 10, maxX = sizeX - 11, minZ = 10, maxZ = sizeZ - 11;
                if (aroundColonists)
                {
                    minX = Math.Max(10, focus.x - 38); maxX = Math.Min(sizeX - 11, focus.x + 38);
                    minZ = Math.Max(10, focus.z - 38); maxZ = Math.Min(sizeZ - 11, focus.z + 38);
                }

                // Насколько колония бедна: в начале игры пещера дешевле открытой базы,
                // потому что естественные стены и крыша уже есть.
                float wealth = 0f;
                try { wealth = map.wealthWatcher.WealthTotal; } catch { }
                int wood = T.Count(map, ThingDefOf.WoodLog);
                int steel = T.Count(map, ThingDefOf.Steel);
                int blocks = T.Count(map, T.Def("BlocksGranite")) + T.Count(map, T.Def("BlocksLimestone"))
                    + T.Count(map, T.Def("BlocksSlate")) + T.Count(map, T.Def("BlocksSandstone"));
                bool poor = wealth < 12000f || wood + steel + blocks < 700;

                float bestScore = -999f;
                for (int x = minX; x <= maxX; x += 5)
                {
                    for (int z = minZ; z <= maxZ; z += 5)
                    {
                        IntVec3 c = new IntVec3(x, 0, z);
                        if (!c.InBounds(map) || c.Fogged(map) || !c.Standable(map)) continue;
                        if (c.GetEdifice(map) != null) continue;

                        int n = 0, buildable = 0, water = 0, rock = 0, roofed = 0, geyser = 0;
                        int mountainRoof = 0, caveFloor = 0, perimeterRock = 0, perimeterOpen = 0;
                        float fert = 0f;
                        foreach (IntVec3 a in GenRadial.RadialCellsAround(c, 6f, true))
                        {
                            if (!a.InBounds(map)) continue;
                            n++;
                            TerrainDef t = a.GetTerrain(map);
                            string tn = t != null && t.defName != null ? t.defName : "";
                            if (tn.IndexOf("Water", StringComparison.OrdinalIgnoreCase) >= 0
                                || tn.IndexOf("Marsh", StringComparison.OrdinalIgnoreCase) >= 0
                                || tn.IndexOf("Ice", StringComparison.OrdinalIgnoreCase) >= 0
                                || tn.IndexOf("Mud", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                water++;
                                continue;
                            }

                            RoofDef rd = null;
                            try { rd = map.roofGrid.RoofAt(a); } catch { }
                            bool mountain = rd != null && (rd.isThickRoof
                                || (rd.defName != null && rd.defName.IndexOf("Rock", StringComparison.OrdinalIgnoreCase) >= 0));
                            if (mountain) mountainRoof++;
                            else if (rd != null) roofed++;

                            Thing edifice = a.GetEdifice(map);
                            bool rockWall = edifice != null && edifice.def != null && edifice.def.mineable;
                            if (rockWall)
                            {
                                rock++;
                                if (a.DistanceTo(c) >= 4f) perimeterRock++;
                            }
                            else
                            {
                                if (a.Standable(map))
                                {
                                    buildable++;
                                    if (mountain) caveFloor++;
                                    fert += Soil.Fertility(map, a);
                                }
                                if (a.DistanceTo(c) >= 4f) perimeterOpen++;
                            }

                            foreach (Thing th in map.thingGrid.ThingsAt(a))
                            {
                                if (th == null || th.def == null) continue;
                                if (th.def.mineable && !rockWall) rock++;
                                string dn = th.def.defName != null ? th.def.defName : "";
                                if (dn.IndexOf("Geyser", StringComparison.OrdinalIgnoreCase) >= 0) geyser++;
                            }
                        }
                        bool caveLikely = mountainRoof >= 12 && caveFloor >= 8 && perimeterRock >= 6;
                        bool hollow = caveLikely && perimeterRock >= 8 && perimeterOpen <= 10 && caveFloor >= 10;
                        if (n == 0 || buildable < (caveLikely ? 14 : 24)) continue;

                        float flat = buildable / (float)n;
                        float f = fert / Math.Max(1, n);
                        int edgeDist = Math.Min(Math.Min(x, sizeX - 1 - x), Math.Min(z, sizeZ - 1 - z));
                        float edge = Mathf.Clamp01(edgeDist / 30f);
                        float rockBonus = Mathf.Clamp01(rock / 35f);
                        float waterPenalty = water / (float)n;
                        float roofPenalty = roofed / (float)n;
                        float distPenalty = aroundColonists ? Mathf.Clamp01(c.DistanceTo(focus) / 100f) * 0.12f : 0f;

                        // Пещера/скальный карман: естественная крыша и стены дают дешёвую
                        // защиту и укрытие. Ранней бедной колонии это выгоднее открытого поля.
                        float caveBonus = 0f;
                        if (caveLikely)
                        {
                            float mountainShare = mountainRoof / (float)n;
                            float wallShare = perimeterRock / (float)Math.Max(1, perimeterRock + perimeterOpen);
                            caveBonus = 0.14f + Mathf.Clamp01(mountainShare / 0.55f) * 0.10f
                                + wallShare * 0.10f;
                            if (poor) caveBonus += 0.20f;
                            else caveBonus -= 0.03f;                  // богатой колонии пещера не так нужна
                            if (perimeterOpen <= 6) caveBonus += 0.06f; // узкий вход удобно держать
                            if (hollow) caveBonus += 0.05f;
                            if (f < 0.35f) caveBonus -= 0.04f;         // в пещере мало почвы: нужна охота/гидропоника
                        }
                        else if (poor)
                        {
                            caveBonus -= 0.04f;                        // открытое поле в начале дороже по материалам
                        }

                        float score = flat * 0.38f + Mathf.Clamp01(f / 0.9f) * 0.22f + rockBonus * 0.12f
                            + edge * 0.12f + Mathf.Clamp01(geyser / 2f) * 0.06f + caveBonus
                            - waterPenalty * 0.25f - roofPenalty * 0.05f - distPenalty;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best.center = c;
                            best.score = score;
                            best.buildable = buildable;
                            best.fertility = f;
                            best.rock = rock;
                            best.water = water;
                            best.roofed = roofed;
                            best.mountainRoof = mountainRoof;
                            best.naturalRock = rock;
                            best.caveFloor = caveFloor;
                            best.perimeterRock = perimeterRock;
                            best.perimeterOpen = perimeterOpen;
                            best.cave = caveLikely;
                            best.hollow = hollow;
                            best.poor = poor;
                            best.wealth = wealth;
                            best.kind = caveLikely
                                ? (hollow ? "углубление в скале" : "пещера/скальный карман")
                                : "открытое место";
                            best.why = "ровных " + Mathf.RoundToInt(flat * 100f) + "%, плодородие "
                                + Mathf.RoundToInt(f * 100f) + "%, камня " + rock + ", воды " + water
                                + ", горной крыши " + mountainRoof + ", проходов " + perimeterOpen
                                + (poor ? ", начальный бюджет" : "") + ", до края " + edgeDist;
                        }
                    }
                }
                if (!best.center.IsValid)
                {
                    best.center = cols.Count > 0 ? cols[0].Position : map.Center;
                    best.why = "не нашёл ровной площадки — центр у колонистов";
                }
                best.line = "База: " + best.kind + " — центр (" + best.center.x + ", " + best.center.z + ") — " + best.why
                    + "; оценка места " + U.Pct(Mathf.Clamp01(best.score));
                Mind.Think("база", best.line);
            }
            catch (Exception e)
            {
                T.Warn("Выбор места базы: " + e.Message);
                best.center = map.Center;
                best.kind = "центр карты";
                best.why = "ошибка оценки рельефа";
                best.line = "База: центр карты (оценка рельефа недоступна).";
            }
            return best;
        }
    }
}
