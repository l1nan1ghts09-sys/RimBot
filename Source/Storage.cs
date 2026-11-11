using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>
    /// Склады и порча: общий склад у базы, отдельный холодный/крытый склад для еды,
    /// контроль сроков годности и перекладывание продуктов в приоритетное хранение.
    /// </summary>
    public static class Storage
    {
        private static int next;
        private static int lastReport;

        public static void ResetRuntime() { next = 0; lastReport = 0; }

        public static void Run(Map map, int tick)
        {
            if (map == null) return;
            TradeSettings s = TradeEmpireMod.S;
            if (s == null || !s.autoStorage) return;
            if (tick < next) return;
            next = tick + 6000;
            if (!Utility.WeighOnly("storage", "проверить склады и порчу", 0.50f, 0.08f, 0.02f, 1f,
                "еда и вещи будут храниться правильно и меньше испортятся", "продукты сгниют, а склад забьётся")) return;

            T.Safe("StorageGeneral", () => EnsureGeneral(map));
            T.Safe("StorageSheltered", () => EnsureSheltered(map));
            T.Safe("StorageFood", () => EnsureFood(map));
            T.Safe("StorageSpoil", () => Spoilage(map, tick));
        }

        private static List<Zone_Stockpile> Piles(Map map)
        {
            return map.zoneManager.AllZones.OfType<Zone_Stockpile>().ToList();
        }

        // ------------------------------------------------------------ общий склад

        private static void EnsureGeneral(Map map)
        {
            if (Piles(map).Count > 0) return;

            IntVec3 anchor = Infrastructure.Anchor(map);
            List<IntVec3> cells = PickCells(map, anchor, 7f, 25, false);
            if (cells.Count < 6) return;

            Zone_Stockpile zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            for (int i = 0; i < cells.Count; i++) zone.AddCell(cells[i]);
            zone.settings.Priority = StoragePriority.Preferred;
            Mind.Think("быт", "Создал общий склад на " + cells.Count + " клеток у базы: вещи больше не валяются под открытым небом.");
        }

        // ------------------------------------------------------------ склад еды

        public static void EnsureFood(Map map)
        {
            List<Zone_Stockpile> piles = Piles(map);
            Zone_Stockpile food = piles.FirstOrDefault(z => z.label != null && z.label.StartsWith("Еда (бот)"));
            if (food != null)
            {
                // Еду держим только в холоде/под крышей, из общего склада её убираем.
                foreach (Zone_Stockpile z in piles)
                {
                    if (z == food || z.settings == null || z.settings.filter == null) continue;
                    z.settings.filter.SetAllow(ThingCategoryDefOf.Foods, false);
                }
                return;
            }

            IntVec3 anchor = Infrastructure.Anchor(map);
            List<IntVec3> cells = PickCells(map, anchor, 14f, 9, true);
            if (cells.Count < 4) return;   // комнаты с крышей ещё нет — подождём

            Zone_Stockpile zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            for (int i = 0; i < cells.Count; i++) zone.AddCell(cells[i]);
            zone.label = "Еда (бот)";
            zone.settings.Priority = StoragePriority.Critical;
            zone.settings.filter.SetDisallowAll();
            zone.settings.filter.SetAllow(ThingCategoryDefOf.Foods, true);
            Mind.Think("еда", "Создал продуктовый склад на " + cells.Count + " клеток под крышей (приоритет Critical): еда портится медленнее и всегда в одном месте.");
        }

        public static Zone_Stockpile FoodZone(Map map)
        {
            if (map == null) return null;
            return Piles(map).FirstOrDefault(z => z.label != null && z.label.StartsWith("Еда (бот)"));
        }

        /// <summary>Крытый склад для снаряжения, тканей, произведённых товаров и прочих вещей.</summary>
        public static Zone_Stockpile EnsureSheltered(Map map)
        {
            if (map == null) return null;
            List<Zone_Stockpile> piles = Piles(map);
            Zone_Stockpile zone = piles.FirstOrDefault(z => z.label != null && z.label.StartsWith("Вещи (бот)"));
            if (zone != null) return zone;

            IntVec3 anchor = Infrastructure.Anchor(map);
            List<IntVec3> cells = PickCells(map, anchor, 20f, 16, true);
            if (cells.Count < 4) return null;

            zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            for (int i = 0; i < cells.Count; i++) zone.AddCell(cells[i]);
            zone.label = "Вещи (бот)";
            zone.settings.Priority = StoragePriority.Critical;
            zone.settings.filter.SetAllow(ThingCategoryDefOf.Foods, false);
            Mind.Think("хранение", "Создал крытый склад для вещей на " + cells.Count
                + " клеток (приоритет Critical): снаряжение и товары не будут ветшать под дождём.");
            return zone;
        }

        public static string StatusLine(Map map)
        {
            if (map == null) return "Склады: карта не загружена.";
            List<Zone_Stockpile> piles = Piles(map);
            Zone_Stockpile food = FoodZone(map);
            Zone_Stockpile goods = piles.FirstOrDefault(z => z.label != null && z.label.StartsWith("Вещи (бот)"));
            int foodCells = food != null ? food.cells.Count : 0;
            int goodCells = goods != null ? goods.cells.Count : 0;
            return "Склады: зон " + piles.Count + ", еда " + foodCells + " кл."
                + (food != null && food.cells.Count > 0 ? " (t° " + Mathf.RoundToInt(food.cells.First().GetTemperature(map)) + ")" : "")
                + ", крытые вещи " + goodCells + " кл.";
        }

        // ------------------------------------------------------------ проверка порчи

        private static void Spoilage(Map map, int tick)
        {
            int risky = 0, rotten = 0;
            float worst = 0f;
            string worstLabel = "";
            foreach (Thing t in map.listerThings.AllThings)
            {
                if (t == null || !t.Spawned || t.Destroyed) continue;
                CompRottable rot = t.TryGetComp<CompRottable>();
                if (rot == null || rot.Stage == RotStage.Dessicated) continue;
                float pct = rot.RotProgressPct;
                bool roofed = t.Position.Roofed(map);
                bool warm = t.AmbientTemperature > 0f;
                if (pct >= 0.35f || !roofed || warm) risky++;
                if (rot.Stage == RotStage.Rotting) rotten++;
                if (pct > worst) { worst = pct; worstLabel = t.LabelShort; }
            }

            if (risky == 0) return;
            if (tick - lastReport < 6000) return;
            lastReport = tick;

            string msg = "Порча: " + risky + " скоропортящихся, из них испорчено " + rotten;
            if (worst > 0f) msg += ", хуже всего «" + worstLabel + "» (" + Mathf.RoundToInt(worst * 100f) + "% пути до порчи)";
            msg += ". Держу еду в крытом складе с приоритетом Critical";
            if (map.mapTemperature.OutdoorTemp > 2f) msg += "; для долгого хранения нужно исследовать кондиционирование и собрать холодильник";
            msg += ".";
            Mind.Think("еда", msg);
            Knowledge.Journal("ПОРЧА: " + msg);
        }

        // ------------------------------------------------------------ помощники

        private static List<IntVec3> PickCells(Map map, IntVec3 center, float radius, int max, bool roofed)
        {
            List<IntVec3> list = new List<IntVec3>();
            foreach (IntVec3 c in GenRadial.RadialCellsAround(center, radius, true))
            {
                if (list.Count >= max) break;
                if (!c.InBounds(map) || c.Fogged(map) || !c.Standable(map)) continue;
                if (roofed && !c.Roofed(map)) continue;
                if (c.GetEdifice(map) != null) continue;
                if (map.zoneManager.ZoneAt(c) != null) continue;
                bool blocked = false;
                foreach (Thing t in map.thingGrid.ThingsAt(c))
                    if (!t.def.CanOverlapZones) { blocked = true; break; }
                if (blocked) continue;
                list.Add(c);
            }
            return list;
        }
    }
}
