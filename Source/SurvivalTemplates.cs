using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>
    /// Шаблоны выживания. Бот выбирает один понятный сценарий под текущую угрозу:
    /// голод, зима, холод, жара, токсичные осадки, пожар, эпидемия, блайт, вулканическая
    /// зима, потеря энергии, плохое настроение или старт с нуля. Шаблон не заменяет
    /// остальные модули, а задаёт приоритеты и конкретные действия.
    /// </summary>
    public static class SurvivalTemplates
    {
        public enum Kind
        {
            None, Steady, Opening, FoodEmergency, FoodSecure, Cold, Heat, ToxicFallout,
            Wildfire, Epidemic, Blight, Winter, VolcanicWinter, PowerLoss, Mood
        }

        public class Report
        {
            public Kind active = Kind.None;
            public string name = "наблюдение";
            public string why = "";
            public string actions = "";
            public float score;
            public string line = "Выживание: наблюдаю за колонией.";
        }

        private class Context
        {
            public Map map;
            public int tick, pop, day;
            public float foodDays, targetDays, temp, mood;
            public int starving, hypothermia, heatstroke, downed, needsTend, bleeding, blight, fires;
            public bool winter, toxic, coldSnap, heatWave, volcanic, eclipse, solarFlare;
            public Pawn anchor;
        }

        private static readonly Dictionary<Kind, float> scores = new Dictionary<Kind, float>();
        private static Kind active = Kind.None;
        private static int changedTick = -99999;
        private static int next, lastActionTick = -99999;
        private static Report last = new Report();

        public static Report Last { get { return last; } }
        public static string Line(Map map) { return last != null ? last.line : "Выживание: наблюдаю за колонией."; }

        public static void ResetRuntime()
        {
            active = Kind.None;
            changedTick = -99999;
            next = 0;
            lastActionTick = -99999;
            scores.Clear();
            last = new Report();
        }

        public static void Run(Map map, int tick)
        {
            if (map == null) return;
            if (map.mapPawns.FreeColonistsSpawnedCount == 0) return;   // нечего выживать
            TradeSettings s = TradeEmpireMod.S;
            if (s != null && !s.autoSurvival) { last.line = "Выживание: шаблоны выключены в настройках."; return; }
            if (tick <= 0) tick = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            if (tick < next) return;
            next = tick + 1800;

            try
            {
                Context c = Read(map, tick);
                Kind best = Choose(c);
                if (best != active)
                {
                    active = best;
                    changedTick = tick;
                    last.name = Name(best);
                    last.why = Why(best, c);
                    Mind.Think("выживание", "Шаблон «" + last.name + "»: " + last.why);
                }
                last.active = active;
                last.score = ScoreOf(active);
                if (tick - lastActionTick >= 3000)
                {
                    lastActionTick = tick;
                    Apply(last, c);
                }
                last.line = "Выживание: шаблон «" + last.name + "» — " + last.why
                    + (string.IsNullOrEmpty(last.actions) ? "" : " Действия: " + last.actions + ".");
            }
            catch (Exception e)
            {
                T.Warn("Шаблоны выживания: " + e.Message);
                last.line = "Выживание: ошибка шаблона — " + e.Message;
            }
        }

        // ============================== выбор шаблона ==============================

        private static Context Read(Map map, int tick)
        {
            Context c = new Context { map = map, tick = tick };
            c.pop = Math.Max(1, map.mapPawns.FreeColonistsSpawnedCount);
            c.day = tick / 60000;
            c.anchor = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
            c.targetDays = Mathf.Clamp(8f + c.pop * 0.4f, 8f, 22f);
            c.foodDays = 0f;
            try { c.foodDays = FoodSafety.Analyze(map).daysFood; } catch { }
            try
            {
                foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
                {
                    if (p == null) continue;
                    if (p.Downed) c.downed++;
                    if (p.needs != null && p.needs.food != null && p.needs.food.CurLevelPercentage < 0.15f) c.starving++;
                    if (p.needs != null && p.needs.mood != null) c.mood += p.needs.mood.CurLevelPercentage;
                    if (p.health != null && p.health.hediffSet != null)
                    {
                        if (p.health.hediffSet.HasHediff(HediffDefOf.Hypothermia)) c.hypothermia++;
                        if (p.health.hediffSet.HasHediff(HediffDefOf.Heatstroke)) c.heatstroke++;
                        if (p.health.HasHediffsNeedingTend()) c.needsTend++;
                        if (p.health.hediffSet.BleedRateTotal > 0.1f) c.bleeding++;
                    }
                }
            }
            catch { }
            c.mood = c.pop > 0 ? c.mood / c.pop : 1f;
            c.temp = 20f;
            try { c.temp = map.mapTemperature.OutdoorTemp; } catch { }
            try { c.fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count; } catch { }
            c.winter = Agriculture.IsWinter(map);
            c.toxic = Agriculture.HasCondition(map, "toxic");
            c.coldSnap = Agriculture.HasCondition(map, "coldsnap") || Agriculture.HasCondition(map, "cold snap");
            c.heatWave = Agriculture.HasCondition(map, "heatwave") || Agriculture.HasCondition(map, "heat wave");
            c.volcanic = Agriculture.HasCondition(map, "volcanic");
            c.eclipse = Agriculture.HasCondition(map, "eclipse");
            c.solarFlare = Agriculture.HasCondition(map, "solarflare") || Agriculture.HasCondition(map, "solar flare");
            try { c.blight = Agriculture.Analyze(map).blighted; } catch { }
            return c;
        }

        private static Kind Choose(Context c)
        {
            scores.Clear();
            Add(Kind.Wildfire, c.fires > 0 ? 5f : 0f);
            Add(Kind.FoodEmergency, c.starving > 0 ? 4.6f : c.foodDays < 2.5f ? 4.0f : 0f);
            Add(Kind.ToxicFallout, c.toxic ? 3.5f : 0f);
            Add(Kind.Epidemic, (c.downed > 0 ? 2.2f : 0f) + (c.bleeding > 0 ? 0.9f : 0f) + (c.needsTend > 0 ? 0.8f : 0f));
            Add(Kind.Cold, (c.hypothermia > 0 ? 2.0f : 0f) + (c.temp < -8f ? 1.2f : 0f) + (c.coldSnap ? 0.8f : 0f) + (c.winter ? 0.4f : 0f));
            Add(Kind.Heat, (c.heatstroke > 0 ? 2.0f : 0f) + (c.temp > 38f ? 1.2f : 0f) + (c.heatWave ? 0.8f : 0f));
            Add(Kind.Blight, c.blight > 2 ? 2.4f : c.blight > 0 ? 1.4f : 0f);
            Add(Kind.FoodSecure, c.foodDays < c.targetDays ? 1.9f : 0f);
            Add(Kind.VolcanicWinter, c.volcanic ? 2.0f : 0f);
            Add(Kind.PowerLoss, (c.eclipse || c.solarFlare) ? 1.8f : 0f);
            Add(Kind.Winter, c.winter ? 1.3f + (c.foodDays < c.targetDays ? 0.5f : 0f) : 0f);
            Add(Kind.Opening, c.day < 15 && (Opening.Brutal() || c.pop <= 2) ? 1.5f : 0f);
            Add(Kind.Mood, c.mood < 0.32f ? 1.3f : 0f);
            Add(Kind.Steady, 0.25f);

            Kind best = Kind.Steady;
            float bestScore = -1f;
            foreach (var kv in scores)
                if (kv.Value > bestScore) { best = kv.Key; bestScore = kv.Value; }

            // Гистерезис: не дёргаем шаблон каждые полчаса; экстренные смены разрешены сразу.
            if (active != Kind.None && active != best && !Emergency(best) && scores.ContainsKey(active))
            {
                float cur = scores[active];
                if (cur >= bestScore - 0.55f && c.tick - changedTick < 9000) best = active;
            }
            return best;
        }

        private static bool Emergency(Kind k)
        {
            return k == Kind.Wildfire || k == Kind.FoodEmergency || k == Kind.ToxicFallout || k == Kind.Epidemic;
        }

        private static void Add(Kind k, float score)
        {
            scores[k] = Mathf.Max(0f, score);
        }

        private static float ScoreOf(Kind k)
        {
            float v;
            return scores.TryGetValue(k, out v) ? v : 0f;
        }

        private static string Name(Kind k)
        {
            switch (k)
            {
                case Kind.Opening: return "старт с нуля";
                case Kind.FoodEmergency: return "голодный старт";
                case Kind.FoodSecure: return "продовольственная безопасность";
                case Kind.Cold: return "холод и мороз";
                case Kind.Heat: return "жара и перегрев";
                case Kind.ToxicFallout: return "токсичные осадки";
                case Kind.Wildfire: return "пожар";
                case Kind.Epidemic: return "эпидемия и ранения";
                case Kind.Blight: return "блайт растений";
                case Kind.Winter: return "зимняя заготовка";
                case Kind.VolcanicWinter: return "вулканическая зима";
                case Kind.PowerLoss: return "потеря энергии";
                case Kind.Mood: return "риск срыва настроения";
                case Kind.Steady: return "стабильное выживание";
                default: return "наблюдение";
            }
        }

        private static string Why(Kind k, Context c)
        {
            switch (k)
            {
                case Kind.Opening: return "ранний старт, мало людей и ресурсов — сначала еда, дерево, костёр и спальные места.";
                case Kind.FoodEmergency: return "запас " + U.F(c.foodDays) + " дн., голодающих " + c.starving + " — включаю сбор, охоту и быстрый рис.";
                case Kind.FoodSecure: return "запас " + U.F(c.foodDays) + " при цели " + U.F(c.targetDays) + " дн. — расширяю поля и держу еду.";
                case Kind.Cold: return "температура " + Mathf.RoundToInt(c.temp) + "°C, переохлаждение " + c.hypothermia + " — тепло, дерево и крыша важнее новых проектов.";
                case Kind.Heat: return "температура " + Mathf.RoundToInt(c.temp) + "°C, перегрев " + c.heatstroke + " — нужны тень, кулер и работа в помещениях.";
                case Kind.ToxicFallout: return "токсичные осадки: открытые поля и прогулки опасны, держу людей под крышей и сохраняю еду.";
                case Kind.Wildfire: return "очагов огня " + c.fires + " — тушение и разрыв пожара важнее всех планов.";
                case Kind.Epidemic: return "лежачих " + c.downed + ", кровотечений " + c.bleeding + ", нужен врач " + c.needsTend + " — медицина в приоритете.";
                case Kind.Blight: return "блайт на " + c.blight + " растениях — срезаю больные и пересеваю быстрым рисом.";
                case Kind.Winter: return "зима: открытые поля не растут, нужен запас, тепло и заготовка дерева.";
                case Kind.VolcanicWinter: return "вулканическая зима: свет и рост растений падают, держу запас и быстрые культуры.";
                case Kind.PowerLoss: return "затмение/солнечная вспышка: энергия нестабильна, берегу запас и холод.";
                case Kind.Mood: return "среднее настроение " + U.Pct(c.mood) + " — риск срыва, даю отдых и радость.";
                default: return "кризисов нет: расширяю запас, поля и хозяйство без спешки.";
            }
        }

        // ============================== действия ==============================

        private static void Apply(Report r, Context c)
        {
            r.actions = "";
            try
            {
                switch (r.active)
                {
                    case Kind.Opening:
                        Opening.Run(c.map);
                        r.actions = "стартовый набор";
                        break;

                    case Kind.FoodEmergency:
                        r.actions = FoodEmergency(c);
                        break;

                    case Kind.FoodSecure:
                        r.actions = FoodSecure(c);
                        break;

                    case Kind.Cold:
                        r.actions = Cold(c);
                        break;

                    case Kind.Heat:
                        r.actions = Heat(c);
                        break;

                    case Kind.ToxicFallout:
                        r.actions = Toxic(c);
                        break;

                    case Kind.Wildfire:
                        Crisis.Run(c.map);
                        Patterns.FireSafety(c.map);
                        r.actions = "тушение, пожарная пена, приоритет Firefighter";
                        break;

                    case Kind.Epidemic:
                        Crisis.Run(c.map);
                        BotBridge.SetBotSetting("autoMedical", true);
                        r.actions = "врач и лечение в приоритете, запас лекарств";
                        break;

                    case Kind.Blight:
                    {
                        int cut = Agriculture.CutBlighted(c.map);
                        Agriculture.EnsureFields(c.map, false);
                        r.actions = "срезано больных " + cut + ", пересев быстрым рисом";
                        break;
                    }

                    case Kind.Winter:
                        r.actions = Winter(c);
                        break;

                    case Kind.VolcanicWinter:
                        Policy.HoldFood = true;
                        BotBridge.SetBotSetting("autoGrowing", true);
                        Agriculture.EnsureFields(c.map, false);
                        r.actions = "запас еды, быстрые культуры, запрет продажи еды";
                        break;

                    case Kind.PowerLoss:
                        PowerManager.Run(c.map);
                        Policy.HoldFood = true;
                        r.actions = "баланс энергии, запас еды без холода";
                        break;

                    case Kind.Mood:
                        Needs.Run(c.map);
                        r.actions = "отдых и радость по показаниям";
                        break;

                    default:
                        r.actions = "";
                        break;
                }
            }
            catch (Exception e) { T.Warn("Действие шаблона «" + r.name + "»: " + e.Message); }
        }

        private static string FoodEmergency(Context c)
        {
            Policy.HoldFood = true;
            BotBridge.SetBotSetting("autoHunt", true);
            BotBridge.SetBotSetting("autoGrowing", true);
            BotBridge.SetBotSetting("autoCutPlants", true);
            int added = Agriculture.EnsureFields(c.map, true);
            int wild = c.anchor != null ? Opening.DesignateWildFood(c.map, c.anchor.Position, 30) : 0;
            if (c.anchor != null) Opening.DesignateTrees(c.map, c.anchor.Position, 12);
            if (Vanilla.Active) Vanilla.Hunt(c.map);
            Crisis.Run(c.map);
            return "грядки +" + added + ", дикой еды " + wild + ", охота и рубка включены, продажа еды запрещена";
        }

        private static string FoodSecure(Context c)
        {
            Policy.HoldFood = c.foodDays < 4f;
            BotBridge.SetBotSetting("autoGrowing", true);
            int added = Agriculture.EnsureFields(c.map, false);
            if (c.foodDays < c.targetDays * 0.5f) BotBridge.SetBotSetting("autoHunt", true);
            return "грядки +" + added + ", запас " + U.F(c.foodDays) + " из " + U.F(c.targetDays) + " дн.";
        }

        private static string Cold(Context c)
        {
            Crisis.Run(c.map);
            bool warm = EnsureWarm(c.map, c.anchor);
            int wood = 0;
            if (c.anchor != null) wood = Opening.DesignateTrees(c.map, c.anchor.Position, 20);
            BotBridge.SetBotSetting("autoMedical", true);
            return (warm ? "поставил обогрев" : "обогрев уже есть или недоступен")
                + (wood > 0 ? ", пометил деревьев " + wood : "") + ", работа в помещениях";
        }

        private static string Heat(Context c)
        {
            Crisis.Run(c.map);
            bool cool = EnsureCool(c.map);
            return (cool ? "поставил пассивный кулер" : "кулер уже есть или недоступен") + ", работа в тени";
        }

        private static string Toxic(Context c)
        {
            Policy.HoldFood = true;
            Crisis.Run(c.map);
            Agriculture.EnsureFields(c.map, false);
            bool greenhouse = TryGreenhouse(c.map);
            Housing.Build(c.map, false);
            return "укрытие под крышей, только крытые поля, запас еды"
                + (greenhouse ? ", заложил теплицу с лампой" : "");
        }

        private static string Winter(Context c)
        {
            Crisis.Run(c.map);
            bool warm = EnsureWarm(c.map, c.anchor);
            int wood = 0;
            if (c.anchor != null) wood = Opening.DesignateTrees(c.map, c.anchor.Position, 20);
            Agriculture.EnsureFields(c.map, false);
            bool greenhouse = TryGreenhouse(c.map);
            Policy.HoldFood = c.foodDays < c.targetDays;
            return (warm ? "обогрев" : "обогрев уже есть")
                + (wood > 0 ? ", дерево +" + wood : "") + ", запас еды и быстрые культуры"
                + (greenhouse ? ", теплица с лампой" : "");
        }

        /// <summary>
        /// Теплица для зимы и токсичных осадков: солнечная лампа в крытом помещении
        /// с плодородной почвой. Сами грядки Agriculture заложит под лампой.
        /// </summary>
        private static bool TryGreenhouse(Map map)
        {
            if (map == null) return false;
            try
            {
                ThingDef lamp = T.Def("SunLamp");
                if (lamp == null || !Infrastructure.Available(lamp) || Infrastructure.Exists(map, lamp)) return false;
                IntVec3 anchor = Infrastructure.Anchor(map);
                for (int r = 3; r <= 18; r++)
                {
                    foreach (IntVec3 c in GenRadial.RadialCellsAround(anchor, r, true))
                    {
                        if (!c.InBounds(map) || c.Fogged(map) || !c.Roofed(map)) continue;
                        if (Soil.Fertility(map, c) < 0.80f) continue;
                        if (c.GetEdifice(map) != null) continue;
                        int soil = 0;
                        foreach (IntVec3 n in GenRadial.RadialCellsAround(c, 5f, true))
                        {
                            if (!n.InBounds(map) || n.Fogged(map) || !n.Roofed(map)) continue;
                            if (n.GetEdifice(map) != null) continue;
                            if (Soil.Fertility(map, n) >= 0.70f) soil++;
                        }
                        if (soil < 12) continue;
                        if (Infrastructure.PlaceNear(map, lamp, c, 1, 4, true))
                        {
                            Mind.Think("теплица", "Заложил солнечную лампу в крытом помещении: " + soil
                                + " клеток почвы смогут расти зимой и при токсичных осадках.");
                            return true;
                        }
                    }
                }
            }
            catch (Exception e) { T.Warn("Теплица: " + e.Message); }
            return false;
        }

        private static bool EnsureWarm(Map map, Pawn anchor)
        {
            if (map == null) return false;
            IntVec3 at = anchor != null ? anchor.Position : Infrastructure.Anchor(map);
            ThingDef heater = T.Def("Heater");
            try
            {
                if (heater != null && Infrastructure.Available(heater) && !Infrastructure.Exists(map, heater))
                {
                    Building_Bed bed = map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>()
                        .FirstOrDefault(b => b.Position.Roofed(map));
                    IntVec3 center = bed != null ? bed.Position : at;
                    if (Infrastructure.PlaceNear(map, heater, center, 2, 10, center.Roofed(map)))
                    {
                        Mind.Think("тепло", "Ставлю обогреватель у кроватей: на улице " + Mathf.RoundToInt(map.mapTemperature.OutdoorTemp) + "°C.");
                        return true;
                    }
                }
                ThingDef camp = T.Def("Campfire");
                if (camp != null && !Infrastructure.Exists(map, camp)
                    && Infrastructure.PlaceNear(map, camp, at, 2, 10, false))
                    return true;
            }
            catch (Exception e) { T.Warn("Обогрев: " + e.Message); }
            return false;
        }

        private static bool EnsureCool(Map map)
        {
            if (map == null) return false;
            try
            {
                ThingDef cooler = T.Def("PassiveCooler");
                if (cooler == null || !Infrastructure.Available(cooler) || Infrastructure.Exists(map, cooler)) return false;
                Zone_Stockpile food = Storage.FoodZone(map);
                IntVec3 center = food != null && food.cells.Count > 0
                    ? new IntVec3((int)food.cells.Average(c => c.x), 0, (int)food.cells.Average(c => c.z))
                    : Infrastructure.Anchor(map);
                if (Infrastructure.PlaceNear(map, cooler, center, 2, 12, true))
                {
                    Mind.Think("тепло", "Ставлю пассивный кулер: в жару еда и люди перегреваются, нужен холод без энергии.");
                    return true;
                }
            }
            catch (Exception e) { T.Warn("Охлаждение: " + e.Message); }
            return false;
        }
    }
}
