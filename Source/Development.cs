using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>Развитие колонии: какие исследования брать следующими и кого назначить исполнителем.</summary>
    public static class Development
    {
        private static int next;
        private static string lastPick = "";

        public static void ResetRuntime() { next = 0; lastPick = ""; }

        public static void Run(Map map, int tick)
        {
            TradeSettings s = TradeEmpireMod.S;
            if (map == null || s == null || !s.autoDevelopment) return;
            if (tick < next) return;
            next = tick + 12000;

            ResearchProjectDef current = BotBridge.CurrentResearch();
            if (current != null && !current.IsFinished) return;
            if (!Utility.WeighOnly("research", "выбрать исследование", 0.50f, 0.07f, 0.02f, 1f,
                "колония получит новые технологии и постройки", "развитие остановится, оборона и крафт отстанут",
                "поставить следующее исследование")) return;

            ResearchProjectDef pick = Choose(map, out string why);
            if (pick == null || pick == current) return;
            if (!BotBridge.SetResearch(pick)) return;
            if (lastPick != pick.defName)
            {
                lastPick = pick.defName;
                Mind.Think("наука", "Развитие: ставлю «" + pick.LabelCap + "» — " + why + ".");
                Knowledge.Journal("РАЗВИТИЕ: исследование «" + pick.LabelCap + "» — " + why + ".");
            }
        }

        private static ResearchProjectDef Choose(Map map, out string why)
        {
            why = "";
            List<KeyValuePair<string, string>> wants = new List<KeyValuePair<string, string>>();

            int comms = map.listerBuildings.allBuildingsColonist.Count(b => b.def.defName == "CommsConsole");
            if (comms == 0) wants.Add(Want("MicroelectronicsBasics", "консоль связи и торговля с орбитой"));

            if (!map.listerBuildings.allBuildingsColonist.Any(b => b.GetComp<CompPowerPlant>() != null))
                wants.Add(Want("Electricity", "электричество для кухни, турелей и освещения"));

            if (map.mapTemperature.OutdoorTemp > 2f && CountPerishable(map) > 20)
                wants.Add(Want("AirConditioning", "холодильник, чтобы еда не портилась"));

            int turrets = map.listerBuildings.allBuildingsColonist.Count(b => b.def.defName != null && b.def.defName.StartsWith("Turret_"));
            if (turrets < 2) wants.Add(Want("GunTurrets", "турели для обороны"));

            int components = T.Count(map, ThingDefOf.ComponentIndustrial);
            if (components < 10) wants.Add(Want("Machining", "свои компоненты вместо покупки"));

            int meds = T.Count(map, ThingDefOf.MedicineIndustrial) + T.Count(map, ThingDefOf.MedicineHerbal);
            if (meds < 8) wants.Add(Want("MedicineProduction", "лекарства для врача"));

            int blocks = 0;
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
                if (d.defName != null && d.defName.StartsWith("Blocks") && d.category == ThingCategory.Item) blocks += T.Count(map, d);
            if (blocks < 100) wants.Add(Want("Stonecutting", "каменные блоки для прочной базы"));

            if (!map.zoneManager.AllZones.OfType<Zone_Growing>().Any())
                wants.Add(Want("Hydroponics", "еда без плодородной почвы"));

            foreach (KeyValuePair<string, string> w in wants)
            {
                ResearchProjectDef p = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(w.Key);
                if (p == null || p.IsFinished || !p.CanStartNow) continue;
                why = w.Value;
                return p;
            }

            // Ничего обязательного не осталось — берём лучший проект по оценке ценности.
            DefExpert.ResearchPick best = DefExpert.BestResearch(BrainComponent.Get() != null ? BrainComponent.Get().World : null);
            if (best != null && best.project != null)
            {
                why = string.IsNullOrEmpty(best.why) ? "наибольшая польза для колонии" : best.why;
                return best.project;
            }
            return null;
        }

        private static KeyValuePair<string, string> Want(string def, string why)
        {
            return new KeyValuePair<string, string>(def, why);
        }

        private static int CountPerishable(Map map)
        {
            int n = 0;
            foreach (Thing t in map.listerThings.AllThings)
                if (t != null && t.Spawned && t.TryGetComp<CompRottable>() != null) n++;
            return n;
        }
    }
}
