using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>Проверка плодородности почвы и выбор культуры под конкретный участок.</summary>
    public static class Soil
    {
        /// <summary>Ниже этого плодородия грядку не закладываем: гравий и камень дают мало еды.</summary>
        public const float MinFertility = 0.65f;
        /// <summary>Богатая почва: можно сажать рис и кукурузу.</summary>
        public const float GoodFertility = 0.95f;

        public static float Fertility(Map map, IntVec3 c)
        {
            if (map == null || !c.InBounds(map)) return 0f;
            try
            {
                float f = map.fertilityGrid.FertilityAt(c);
                TerrainDef terrain = c.GetTerrain(map);
                if (terrain != null && terrain.fertility > f) f = terrain.fertility;
                return Mathf.Clamp01(f);
            }
            catch { return 0f; }
        }

        /// <summary>Годится ли клетка под грядку: открытая, не занятая, достаточно плодородная.</summary>
        public static bool Suitable(Map map, IntVec3 c, float minFertility)
        {
            if (map == null || !c.InBounds(map) || c.Fogged(map) || c.Roofed(map)) return false;
            if (map.zoneManager.ZoneAt(c) != null) return false;
            if (Fertility(map, c) < minFertility) return false;
            if (c.GetEdifice(map) != null) return false;
            foreach (Thing t in map.thingGrid.ThingsAt(c))
                if (!t.def.CanOverlapZones) return false;
            return true;
        }

        public static float Average(Map map, List<IntVec3> cells)
        {
            if (map == null || cells == null || cells.Count == 0) return 0f;
            float sum = 0f;
            for (int i = 0; i < cells.Count; i++) sum += Fertility(map, cells[i]);
            return sum / cells.Count;
        }

        /// <summary>Рис и кукуруза требуют богатой почвы; картофель выдерживает бедную.</summary>
        public static ThingDef BestCrop(Map map, float avgFertility)
        {
            ThingDef rice = T.Def("Plant_Rice");
            ThingDef corn = T.Def("Plant_Corn");
            ThingDef potato = T.Def("Plant_Potato");

            if (avgFertility >= GoodFertility && corn != null) return corn;
            if (avgFertility >= GoodFertility && rice != null) return rice;
            if (potato != null) return potato;
            return rice ?? corn;
        }

        public static string Percent(float f)
        {
            return Mathf.RoundToInt(Mathf.Clamp01(f) * 100f) + "%";
        }
    }
}
