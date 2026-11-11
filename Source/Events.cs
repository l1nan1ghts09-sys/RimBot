using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>События: пришлые (принять/отклонить), игровые условия, укрытие при токсичных осадках.</summary>
    public static class Events
    {
        private static readonly HashSet<int> seenConditions = new HashSet<int>();
        private static readonly Dictionary<Pawn, Area> previousAreas = new Dictionary<Pawn, Area>();
        private static bool sheltering;

        public static void Run(Map map)
        {
            Joiners(map);
            Conditions(map);
            if (TradeEmpireMod.S.quests) T.Safe("Quests", () => Quests.Run(map));
            if (TradeEmpireMod.S.autoControl) T.Safe("Letters", Control.CleanLetters);
        }

        private static void Joiners(Map map)
        {
            int cols = map.mapPawns.FreeColonistsCount;
            int target = Math.Max(T.BotInt("targetColonists", 10), 6);
            foreach (Letter l in Find.LetterStack.LettersListForReading.ToList())
            {
                var cl = l as ChoiceLetter_AcceptJoiner;
                if (cl == null) continue;
                bool accept = cols < target;
                DiaOption pick = null;
                foreach (DiaOption o in cl.Choices)
                {
                    string t = (o.text ?? "").ToLowerInvariant();
                    bool isAccept = t.Contains("accept") || t.Contains("прин") || t.Contains("join") || t.Contains("прис");
                    bool isReject = t.Contains("reject") || t.Contains("отк") || t.Contains("отклон");
                    if (accept && isAccept) { pick = o; break; }
                    if (!accept && isReject) { pick = o; break; }
                }
                if (pick == null || pick.action == null) continue;
                pick.action();
                Mind.Think("событие", "Пришлый «" + cl.Label + "»: " + (accept ? "принимаю" : "отклоняю") + " (колонистов " + cols + " из " + target + ").");
            }
        }

        private static void Conditions(Map map)
        {
            bool toxic = false;
            foreach (GameCondition gc in map.gameConditionManager.ActiveConditions)
            {
                if (gc.def == GameConditionDefOf.ToxicFallout) toxic = true;
                if (seenConditions.Add(gc.GetHashCode()))
                    Mind.Think("событие", "Началось: " + gc.def.LabelCap + (gc.def == GameConditionDefOf.ToxicFallout ? " — все в укрытие (зона «Укрытие»)." : " — наблюдаю."));
            }
            if (toxic && !sheltering) Shelter(map, true);
            else if (!toxic && sheltering) Shelter(map, false);
        }

        private static void Shelter(Map map, bool on)
        {
            sheltering = on;
            if (on)
            {
                Area_Allowed a = map.areaManager.AllAreas.OfType<Area_Allowed>().FirstOrDefault(x => x.Label == "Укрытие");
                if (a == null)
                {
                    if (!map.areaManager.TryMakeNewAllowed(out a)) { sheltering = false; return; }
                    a.SetLabel("Укрытие");
                }
                var home = map.areaManager.Home;
                if (home != null) foreach (IntVec3 c in home.ActiveCells) if (c.Roofed(map)) a[c] = true;
                foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
                {
                    if (p.playerSettings == null) continue;
                    previousAreas[p] = p.playerSettings.AreaRestrictionInPawnCurrentMap;
                    p.playerSettings.AreaRestrictionInPawnCurrentMap = a;
                }
                Mind.Think("решение", "Токсичные осадки: колонисты заперты в крытых помещениях дома.");
            }
            else
            {
                foreach (var kv in previousAreas)
                    if (kv.Key != null && kv.Key.playerSettings != null) kv.Key.playerSettings.AreaRestrictionInPawnCurrentMap = kv.Value;
                previousAreas.Clear();
                Mind.Think("решение", "Осадки закончились: ограничения снимаю.");
            }
        }
    }
}
