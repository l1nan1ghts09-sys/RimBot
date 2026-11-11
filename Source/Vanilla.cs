using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>
    /// Автономный режим для чистой ванильной игры: без Harmony, DLC и второго DLL.
    /// Берёт на себя то, что раньше делал основной бот: призыв и оборону, охоту,
    /// приручение, посевы, добычу ресурсов, экипировку и базовые задания на верстаках.
    /// Все действия — обычные ванильные приказы (designation / bill / draft / job),
    /// поэтому они работают с любым набором DLC и не требуют патчей.
    /// </summary>
    public static class Vanilla
    {
        private static readonly HashSet<int> botDrafted = new HashSet<int>();
        private static int nextDefense, nextFarm, nextHunt, nextTame, nextMine, nextEquip, nextBills, nextPrison, nextShelter;
        private static int lastThought;
        private static bool announced;

        /// <summary>Основного бота нет — работаем сами.</summary>
        public static bool Active
        {
            get
            {
                try
                {
                    TradeSettings s = TradeEmpireMod.S;
                    if (s == null || !s.vanillaAuto) return false;
                    return !BotBridge.BotPresent;
                }
                catch { return false; }
            }
        }

        public static void ResetRuntime()
        {
            botDrafted.Clear();
            nextDefense = nextFarm = nextHunt = nextTame = nextMine = nextEquip = nextBills = nextPrison = nextShelter = 0;
            Storage.ResetRuntime();
            Development.ResetRuntime();
            Defense.ResetRuntime();
            Focus.ResetRuntime();
            Crafting.ResetRuntime();
            Site.ResetRuntime();
            Utility.ResetRuntime();
            FoodSafety.ResetRuntime();
            Deterioration.ResetRuntime();
            Mechanics.ResetRuntime();
            lastThought = 0;
            announced = false;
        }

        public static void Run(Map map, int tick)
        {
            if (map == null || !Active) return;
            if (!announced)
            {
                announced = true;
                Mind.Think("решение", "Основной бот не найден: включаю полностью автономный ванильный режим — оборона, охота, посевы, добыча, экипировка и задания на верстаках.");
            }

            T.Safe("Focus", () => Focus.Run(map, tick));
            T.Safe("Needs", () => Needs.Run(map));
            T.Safe("VanillaDefense", () => RaidDefense(map, tick));
            T.Safe("DefenseBuild", () => Defense.Run(map, tick));
            if (tick >= nextBills && Focus.Allows("Craft"))
            {
                nextBills = tick + 6000;
                Utility.Weigh("bills", "обновить задания на верстаках", 0.55f, 0.06f, 0.0f, 1f,
                    "верстаки начнут делать нужные вещи и еду", "задания устареют, крафт и готовка встанут",
                    () => Bills(map));
            }
            if (Focus.Allows("Craft")) T.Safe("Crafting", () => Crafting.Run(map, tick));
            if (tick >= nextShelter && Focus.Allows("Shelter"))
            {
                nextShelter = tick + 9000;
                Utility.Weigh("shelter", "построить или достроить жильё", 0.85f, 0.25f, 0.05f, 1f,
                    "появятся стены, дверь и крыша, кровати перестанут мокнуть", "колонисты останутся под открытым небом",
                    () => Shelter(map));
            }
            if (tick >= nextEquip)
            {
                nextEquip = tick + 3000;
                Utility.Weigh("equip", "выдать оружие и экипировку", 0.45f, 0.05f, 0.02f, 1f,
                    "колонисты будут готовы к бою", "безоружные побегут или погибнут",
                    () => Equip(map));
            }
            if (tick >= nextFarm && Focus.Allows("Food"))
            {
                nextFarm = tick + 4000;
                float f = Utility.FoodPressure(map);
                Utility.Weigh("farm", "разбить или расширить грядки", 0.15f + f * 0.75f, 0.10f, 0.02f, 1f,
                    "появится свой урожай и запас еды", "придётся закупать еду или охотиться",
                    () => Farm(map));
            }
            if (tick >= nextHunt && Focus.Allows("Food"))
            {
                nextHunt = tick + 2500;
                float f = Utility.FoodPressure(map);
                Utility.Weigh("hunt", "пометить животных на охоту", 0.20f + f * 0.65f, 0.10f, 0.12f, 1f,
                    "появится мясо без затрат серебра", "запас еды продолжит падать",
                    () => { Hunt(map); });
            }
            if (tick >= nextMine && Focus.Allows("Mine"))
            {
                nextMine = tick + 4000;
                float st = Utility.SteelPressure(map);
                Utility.Weigh("mine", "пометить руду и камень на добычу", 0.15f + st * 0.65f, 0.12f, 0.04f, 1f,
                    "будет сталь и камень для обороны и построек", "стройка встанет из-за нехватки материалов",
                    () => Mine(map));
            }
            if (tick >= nextTame && Focus.Allows("Build"))
            {
                nextTame = tick + 6000;
                Utility.Weigh("tame", "приручить полезных животных", 0.25f, 0.08f, 0.10f, 0.8f,
                    "животные дадут еду, шерсть и помощь", "колония потеряет дешёвый источник ресурсов",
                    () => { Tame(map); });
            }
            if (tick >= nextPrison) { nextPrison = tick + 6000; T.Safe("VanillaPrison", () => Prisoners(map)); }
            if (Focus.Allows("Storage")) T.Safe("Storage", () => Storage.Run(map, tick));
            if (Focus.Allows("Research")) T.Safe("Development", () => Development.Run(map, tick));
            T.Safe("FoodSafety", () => FoodSafety.Run(map, tick));
            T.Safe("Deterioration", () => Deterioration.Run(map, tick));
            T.Safe("Mechanics", () => Mechanics.Run(map, tick));
        }

        // ============================== оборона ==============================

        private static List<Pawn> Hostiles(Map map)
        {
            List<Pawn> list = new List<Pawn>();
            try
            {
                foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                {
                    if (p == null || !p.Spawned || p.Dead || p.Downed) continue;
                    if (!p.HostileTo(Faction.OfPlayer)) continue;
                    if (!GenHostility.IsActiveThreatToPlayer(p)) continue;
                    list.Add(p);
                }
            }
            catch { }
            return list;
        }

        private static bool CanFight(Pawn p)
        {
            try
            {
                if (p == null || p.Dead || p.Downed || p.InMentalState || p.Drafted) return false;
                if (p.WorkTagIsDisabled(WorkTags.Violent)) return false;
                if (p.health == null || p.health.summaryHealth == null) return false;
                if (p.health.summaryHealth.SummaryHealthPercent < 0.4f) return false;
                if (p.health.hediffSet != null && p.health.hediffSet.BleedRateTotal > 0.06f) return false;
                return true;
            }
            catch { return false; }
        }

        private static bool HasGun(Pawn p)
        {
            try
            {
                return p.equipment != null && p.equipment.Primary != null
                    && p.equipment.Primary.def.IsRangedWeapon;
            }
            catch { return false; }
        }

        private static void RaidDefense(Map map, int tick)
        {
            if (tick < nextDefense) return;
            nextDefense = tick + 60;

            List<Pawn> hostiles = Hostiles(map);
            if (hostiles.Count == 0)
            {
                ReleaseBotDrafted(map);
                return;
            }

            List<Pawn> cols = new List<Pawn>();
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned) if (CanFight(p)) cols.Add(p);
            if (cols.Count == 0) return;

            IntVec3 rally = Infrastructure.Anchor(map);
            List<IntVec3> cover = Defense.CoverCells(map);
            int coverIndex = 0;
            int gunners = 0;
            foreach (Pawn p in cols)
            {
                if (HasGun(p))
                {
                    gunners++;
                    Draft(p);
                    if (p.drafter != null) R.Set(p.drafter, "FireAtWill", true);
                    IntVec3 pos = cover.Count > 0 ? cover[coverIndex++ % cover.Count] : rally;
                    bool far = p.Position.DistanceToSquared(pos) > 9f;
                    if (far && p.CurJobDef != JobDefOf.Goto && p.CurJobDef != JobDefOf.AttackStatic)
                        p.jobs.StartJob(new Job(JobDefOf.Goto, pos), JobCondition.InterruptForced);
                    continue;
                }

                Pawn target = null;
                float best = float.MaxValue;
                foreach (Pawn h in hostiles)
                {
                    if (h == null || h.Dead) continue;
                    float d = h.Position.DistanceToSquared(p.Position);
                    if (d >= best) continue;
                    if (!p.CanReach(h, PathEndMode.Touch, Danger.Deadly)) continue;
                    best = d; target = h;
                }
                if (target == null) continue;
                Draft(p);
                if (p.CurJob == null || p.CurJob.def != JobDefOf.AttackMelee || p.CurJob.targetA.Thing != target)
                    p.jobs.StartJob(new Job(JobDefOf.AttackMelee, target), JobCondition.InterruptForced);
            }

            if (gunners == 0 && tick - lastThought > 3600)
            {
                lastThought = tick;
                Mind.Think("оборона", "Врагов " + hostiles.Count + ", огнестрельного оружия ни у кого нет — дерусь в ближнем бою и стараюсь одеть колонистов.");
            }
        }

        private static void Draft(Pawn p)
        {
            try
            {
                if (p.drafter == null) return;
                if (!p.Drafted)
                {
                    p.drafter.Drafted = true;
                    botDrafted.Add(p.thingIDNumber);
                }
                p.playerSettings.hostilityResponse = HostilityResponseMode.Attack;
            }
            catch { }
        }

        private static void ReleaseBotDrafted(Map map)
        {
            if (botDrafted.Count == 0) return;
            List<int> ids = botDrafted.ToList();
            foreach (int id in ids)
            {
                Pawn p = null;
                foreach (Pawn c in map.mapPawns.FreeColonistsSpawned) if (c.thingIDNumber == id) { p = c; break; }
                if (p != null && p.drafter != null && p.Drafted) p.drafter.Drafted = false;
                botDrafted.Remove(id);
            }
            Mind.Think("оборона", "Угроза устранена: снимаю призыв и возвращаю колонистов к работе.");
        }

        /// <summary>Подготовка обороны: оценка, укрытия, ловушки, стены, турели и крыши.</summary>
        public static void DefensePrep(Map map)
        {
            if (map == null) return;
            try { Defense.Prepare(map); }
            catch (Exception e) { T.Warn("Подготовка обороны: " + e.Message); }
        }

        // ============================== еда ==============================

        private class HuntTarget
        {
            public Pawn animal;
            public float risk, reward;
            public string why;
        }

        /// <summary>Охота с оценкой риска: хищник ли, шанс ответки, стая, сила, расстояние, взрывчатка.</summary>
        public static int Hunt(Map map)
        {
            try
            {
                if (map == null) return 0;
                TradeSettings s = TradeEmpireMod.S;
                float riskLimit = U.Clamp(s != null ? s.huntRiskLimit : 0.45f, 0.05f, 0.95f);
                int maxTargets = Math.Max(1, s != null ? s.huntMaxTargets : 2);

                if (GenHostility.AnyHostileActiveThreatToPlayer(map))
                {
                    Mind.Think("охота", "Охоту отложил: на карте активная угроза — сначала оборона, животные подождут.");
                    return 0;
                }

                int marked = 0;
                foreach (Designation d in map.designationManager.AllDesignations) if (d.def == DesignationDefOf.Hunt) marked++;
                if (marked >= maxTargets) return 0;

                Pawn hunter = BestHunter(map);
                if (hunter == null)
                {
                    Mind.Think("охота", "Охоту пропустил: нет здорового колониста с оружием, который может идти на зверя.");
                    return 0;
                }

                IntVec3 anchor = Infrastructure.Anchor(map);
                var targets = new List<HuntTarget>();
                int tooRisky = 0;
                foreach (Pawn a in map.mapPawns.AllPawnsSpawned)
                {
                    if (a == null || !a.Spawned || a.Dead || a.Downed) continue;
                    if (a.RaceProps == null || !a.RaceProps.Animal || !a.RaceProps.IsFlesh) continue;
                    if (a.RaceProps.Insect) continue;
                    if (a.Faction == Faction.OfPlayer || a.HostFaction == Faction.OfPlayer) continue;
                    if (a.InAggroMentalState) continue;                       // уже бешеный — не трогаем
                    if (!a.Position.InHorDistOf(anchor, 55f)) continue;
                    if (map.designationManager.DesignationOn(a, DesignationDefOf.Hunt) != null) continue;

                    float risk = HuntRisk(a, hunter, map, anchor);
                    if (risk > riskLimit) { tooRisky++; continue; }
                    float reward = Math.Max(0.15f, a.BodySize) * (RB(a.RaceProps, "predator", false) ? 1.5f : 1f);
                    targets.Add(new HuntTarget { animal = a, risk = risk, reward = reward, why = HuntWhy(a, map, anchor) });
                }

                // Сначала выгодные и безопасные: много мяса при малом риске.
                targets.Sort((x, y) => (y.reward / (0.15f + y.risk)).CompareTo(x.reward / (0.15f + x.risk)));

                int made = 0;
                foreach (HuntTarget t in targets)
                {
                    if (made >= maxTargets - marked) break;
                    map.designationManager.AddDesignation(new Designation(t.animal, DesignationDefOf.Hunt));
                    made++;
                    Mind.Think("охота", "Охота: " + t.animal.Label + " — риск " + U.Pct(t.risk) + " (" + t.why
                        + "), мяса ~" + U.F(t.reward) + "; охотник " + hunter.LabelShort
                        + " (стрельба " + hunter.skills.GetSkill(SkillDefOf.Shooting).Level + ").");
                }
                if (made == 0)
                    Mind.Think("охота", "Охота: безопасных целей нет — " + tooRisky + " животных выше лимита риска "
                        + U.Pct(riskLimit) + ". Ждём, пока подойдут ближе или поправятся охотники.");
                return made;
            }
            catch (Exception e) { T.Warn("Охота: " + e.Message); return 0; }
        }

        /// <summary>Лучший охотник: не ранен, не в панике, не призван, умеет стрелять; оружие — плюс.</summary>
        private static Pawn BestHunter(Map map)
        {
            Pawn best = null; float bestScore = -999f;
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p == null || p.Dead || p.Downed || p.InMentalState || p.Drafted) continue;
                if (p.WorkTagIsDisabled(WorkTags.Violent)) continue;
                if (p.health == null || p.health.summaryHealth.SummaryHealthPercent < 0.55f) continue;
                int skill = p.skills != null ? p.skills.GetSkill(SkillDefOf.Shooting).Level : 0;
                bool armed = p.equipment != null && p.equipment.Primary != null;
                float score = skill + (armed ? 10f : 0f) + p.health.summaryHealth.SummaryHealthPercent * 5f;
                if (score > bestScore) { bestScore = score; best = p; }
            }
            return best;
        }

        /// <summary>0 — совсем безопасно, 1 — почти верная смерть охотника.</summary>
        private static float HuntRisk(Pawn a, Pawn hunter, Map map, IntVec3 anchor)
        {
            RaceProperties rp = a.RaceProps;
            bool predator = RB(rp, "predator", false);
            float manhunter = RF(rp, "manhunterOnDamageChance", 0.05f);   // шанс ответки/бешенства при ранении
            float wildness = RF(rp, "wildness", 0.35f);
            float power = RF(a.kindDef, "combatPower", 10f);
            int shooting = hunter.skills != null ? hunter.skills.GetSkill(SkillDefOf.Shooting).Level : 0;
            bool armed = hunter.equipment != null && hunter.equipment.Primary != null;
            float hunterPower = 12f + shooting * 2.5f + (armed ? 12f : -4f);

            float risk = 0f;
            risk += U.Clamp01(manhunter) * 0.30f;                          // пассивка: чем выше шанс ответки, тем опаснее
            risk += predator ? 0.28f : 0f;                                 // хищник сам идёт в атаку
            risk += U.Clamp01(wildness) * 0.08f;
            risk += U.Clamp01(power / Math.Max(20f, hunterPower)) * 0.22f; // сила зверя против силы охотника
            risk += U.Clamp01(a.BodySize / 3f) * 0.12f;
            float dist = a.Position.DistanceTo(anchor);
            risk += U.Clamp01((dist - 25f) / 35f) * 0.15f;                 // далеко от базы — помощь не придёт
            int pack = PackSize(a, map);
            risk += U.Clamp01(pack / 5f) * 0.20f;                          // стая мстит за сородича
            if (a.def != null && a.def.defName != null && a.def.defName.IndexOf("Boom", StringComparison.OrdinalIgnoreCase) >= 0)
                risk += 0.55f;                                             // взрывные — отдельная беда
            if (hunter.health != null) risk += (1f - hunter.health.summaryHealth.SummaryHealthPercent) * 0.20f;
            if (a.health != null && a.health.summaryHealth.SummaryHealthPercent < 0.6f) risk -= 0.05f; // уже ранен
            return U.Clamp01(risk);
        }

        private static string HuntWhy(Pawn a, Map map, IntVec3 anchor)
        {
            RaceProperties rp = a.RaceProps;
            bool predator = RB(rp, "predator", false);
            float manhunter = RF(rp, "manhunterOnDamageChance", 0.05f);
            string why = predator ? "хищник" : "пассивный";
            if (manhunter > 0.02f) why += ", ответка " + U.Pct(manhunter);
            int pack = PackSize(a, map);
            if (pack > 0) why += ", стая " + pack;
            float dist = a.Position.DistanceTo(anchor);
            if (dist > 25f) why += ", далеко " + (int)dist;
            if (a.def != null && a.def.defName != null && a.def.defName.IndexOf("Boom", StringComparison.OrdinalIgnoreCase) >= 0) why += ", взрыв";
            return why;
        }

        private static int PackSize(Pawn a, Map map)
        {
            int n = 0;
            foreach (Pawn o in map.mapPawns.AllPawnsSpawned)
                if (o != null && o != a && !o.Dead && !o.Downed && o.def == a.def && o.Position.InHorDistOf(a.Position, 12f)) n++;
            return n;
        }

        private static bool RB(object target, string member, bool fallback)
        {
            object v = R.Get(target, member);
            if (v is bool) return (bool)v;
            if (v == null) return fallback;
            try { return Convert.ToSingle(v, System.Globalization.CultureInfo.InvariantCulture) > 0.5f; }
            catch { return fallback; }
        }

        private static float RF(object target, string member, float fallback)
        {
            object v = R.Get(target, member);
            if (v == null) return fallback;
            try { return Convert.ToSingle(v, System.Globalization.CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static void Farm(Map map)
        {
            try
            {
                int cells = 0;
                foreach (Zone z in map.zoneManager.AllZones) { Zone_Growing g = z as Zone_Growing; if (g != null) cells += g.cells.Count; }
                int want = 40 + map.mapPawns.FreeColonistsSpawnedCount * 10;
                if (cells >= want) return;

                IntVec3 anchor = Infrastructure.Anchor(map);
                List<IntVec3> candidates = new List<IntVec3>();
                foreach (IntVec3 c in GenRadial.RadialCellsAround(anchor, 28f, true))
                    if (Soil.Suitable(map, c, Soil.MinFertility)) candidates.Add(c);

                if (candidates.Count < 6)
                {
                    if (cells == 0) Mind.Think("еда", "Рядом с базой нет почвы с плодородием выше " + Soil.Percent(Soil.MinFertility)
                        + " — грядку не закладываю, ищу еду охотой и сбором.");
                    return;
                }

                // Сеем компактным участком вокруг самой плодородной клетки: так удобнее носить урожай.
                IntVec3 seed = candidates
                    .OrderByDescending(c => Soil.Fertility(map, c))
                    .ThenBy(c => c.DistanceToSquared(anchor))
                    .First();

                List<IntVec3> pick = candidates
                    .Where(c => c.DistanceTo(seed) <= 6f)
                    .OrderByDescending(c => Soil.Fertility(map, c))
                    .ThenBy(c => c.DistanceToSquared(seed))
                    .Take(24)
                    .ToList();

                if (pick.Count < 6) pick = candidates.OrderByDescending(c => Soil.Fertility(map, c)).Take(24).ToList();
                if (pick.Count < 6) return;

                float avg = Soil.Average(map, pick);
                if (avg < 0.8f)
                {
                    Mind.Think("еда", "Лучший участок рядом с базой даёт всего " + Soil.Percent(avg)
                        + " плодородия — грядку не закладываю, чтобы не тратить работу впустую.");
                    return;
                }

                Zone_Growing zone = new Zone_Growing(map.zoneManager);
                map.zoneManager.RegisterZone(zone);
                for (int i = 0; i < pick.Count; i++) zone.AddCell(pick[i]);
                zone.allowSow = true;

                ThingDef crop = Soil.BestCrop(map, avg);
                if (crop != null) zone.SetPlantDefToGrow(crop);
                Mind.Think("еда", "Создал грядку на " + pick.Count + " клеток, среднее плодородие " + Soil.Percent(avg)
                    + " (" + Soil.Percent(Soil.Fertility(map, seed)) + " в лучшей клетке). Культура: "
                    + (crop != null ? crop.label : "посевы") + ".");
            }
            catch (Exception e) { T.Warn("Посевы: " + e.Message); }
        }

        private static void Bills(Map map)
        {
            try
            {
                int pop = Math.Max(1, map.mapPawns.FreeColonistsSpawnedCount);
                foreach (Building b in map.listerBuildings.allBuildingsColonist)
                {
                    Building_WorkTable t = b as Building_WorkTable;
                    if (t == null || t.BillStack == null) continue;
                    string n = t.def.defName;
                    if (n == "TableButcher") EnsureBill(t, "ButcherCorpseFlesh", BillRepeatModeDefOf.RepeatCount, 10);
                    else if (n == "ElectricStove" || n == "FueledStove" || n == "Campfire")
                    {
                        int meals = pop * 4 + 6;
                        if (FoodSafety.Last != null && FoodSafety.Last.totalNutrition > 0f && FoodSafety.Last.spoilPct > 0.15f)
                            meals = Math.Max(pop * 2, Math.Min(meals, pop * 3));
                        EnsureBill(t, pop >= 6 ? "CookMealFine" : "CookMealSimple", BillRepeatModeDefOf.TargetCount, meals);
                    }
                    else if (n == "TableStonecutter") EnsureBill(t, "MakeStoneBlocks", BillRepeatModeDefOf.TargetCount, 60);
                    else if (n == "TableMachining") EnsureBill(t, "Make_ComponentIndustrial", BillRepeatModeDefOf.TargetCount, 5);
                }
            }
            catch (Exception e) { T.Warn("Задания на верстаках: " + e.Message); }
        }

        private static void EnsureBill(Building_WorkTable table, string recipeName, BillRepeatModeDef mode, int count)
        {
            RecipeDef recipe = DefDatabase<RecipeDef>.GetNamedSilentFail(recipeName);
            if (recipe == null) return;
            foreach (Bill b in table.BillStack.Bills) if (b.recipe == recipe) return;
            Bill_Production bill = recipe.MakeNewBill() as Bill_Production;
            if (bill == null) return;
            bill.repeatMode = mode;
            if (mode == BillRepeatModeDefOf.TargetCount) bill.targetCount = count;
            else bill.repeatCount = count;
            table.BillStack.AddBill(bill);
            Mind.Think("работа", "Добавил задание «" + recipe.label + "» на «" + table.def.label + "» (бот не найден — делаю сам).");
        }

        private static void Mine(Map map)
        {
            try
            {
                if (T.Count(map, ThingDefOf.Steel) >= 300) return;
                int mine = 0;
                foreach (Designation d in map.designationManager.AllDesignations) if (d.def == DesignationDefOf.Mine) mine++;
                if (mine >= 8) return;

                IntVec3 anchor = Infrastructure.Anchor(map);
                int made = 0;
                foreach (Thing t in map.listerThings.AllThings)
                {
                    if (made + mine >= 8) break;
                    if (t == null || !t.Spawned || t.Destroyed || !t.def.mineable) continue;
                    if (!t.Position.InHorDistOf(anchor, 45f)) continue;
                    if (map.designationManager.DesignationOn(t, DesignationDefOf.Mine) != null) continue;
                    map.designationManager.AddDesignation(new Designation(t, DesignationDefOf.Mine));
                    made++;
                }
                if (made > 0) Mind.Think("работа", "Пометил на добычу " + made + " жил/залежей: нужна сталь для стройки и турелей.");
            }
            catch (Exception e) { T.Warn("Добыча: " + e.Message); }
        }

        public static int Tame(Map map)
        {
            try
            {
                int marked = 0;
                foreach (Designation d in map.designationManager.AllDesignations) if (d.def == DesignationDefOf.Tame) marked++;
                if (marked >= 2) return 0;
                int tamed = 0;
                foreach (Pawn a in map.mapPawns.AllPawnsSpawned)
                    if (a.RaceProps != null && a.RaceProps.Animal && a.Faction == Faction.OfPlayer) tamed++;
                if (tamed >= 4) return 0;

                IntVec3 anchor = Infrastructure.Anchor(map);
                int made = 0;
                foreach (Pawn a in map.mapPawns.AllPawnsSpawned)
                {
                    if (made >= 2) break;
                    if (a == null || !a.Spawned || a.Dead || a.Downed) continue;
                    if (a.RaceProps == null || !a.RaceProps.Animal || a.RaceProps.Insect) continue;
                    if (a.Faction != null || a.HostFaction != null) continue;
                    object wv = R.Get(a.RaceProps, "wildness");
                    float wild = wv is float ? (float)wv : 0.4f;
                    if (wild > 0.65f) continue;
                    if (!a.Position.InHorDistOf(anchor, 45f)) continue;
                    if (map.designationManager.DesignationOn(a, DesignationDefOf.Tame) != null) continue;
                    map.designationManager.AddDesignation(new Designation(a, DesignationDefOf.Tame));
                    made++;
                }
                if (made > 0) Mind.Think("животные", "Пометил на приручение " + made + " животных: еда, шерсть и помощь в бою.");
                return made;
            }
            catch (Exception e) { T.Warn("Приручение: " + e.Message); return 0; }
        }

        private static void Prisoners(Map map)
        {
            try
            {
                foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                {
                    if (p == null || p.guest == null) continue;
                    R.Set(p.guest, "ExclusiveInteractionMode", PrisonerInteractionModeDefOf.AttemptRecruit);
                }
            }
            catch (Exception e) { T.Warn("Пленные: " + e.Message); }
        }

        // ============================== жильё ==============================

        /// <summary>Простая комната у базы: стены, дверь и крыша. Без неё кровати, плита и еда остаются под открытым небом.</summary>
        public static void Shelter(Map map)
        {
            try
            {
                int pop = Math.Max(1, map.mapPawns.FreeColonistsSpawnedCount);
                int roofedBeds = 0;
                foreach (Building_Bed b in map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>())
                    if (b.Position.Roofed(map)) roofedBeds++;
                if (roofedBeds >= Math.Min(pop, 3)) return;

                IntVec3 anchor = Infrastructure.Anchor(map);
                int roofed = 0;
                foreach (IntVec3 c in GenRadial.RadialCellsAround(anchor, 12f, true))
                    if (c.InBounds(map) && c.Roofed(map)) roofed++;
                if (roofed >= 24) return;   // уже есть крытое помещение — пусть им занимается навык «быт»

                int side = pop <= 3 ? 7 : pop <= 6 ? 9 : 11;
                for (int r = 0; r <= 15; r += 3)
                {
                    foreach (IntVec3 center in GenRadial.RadialCellsAround(anchor, r, true))
                    {
                        if (!center.InBounds(map)) continue;
                        if (TryRoom(map, center, side)) return;
                        if (RoofIfEnclosed(map, center, side)) return;
                    }
                    if (r == 0) continue;
                }
            }
            catch (Exception e) { T.Warn("Жильё: " + e.Message); }
        }

        private static bool TryRoom(Map map, IntVec3 center, int side)
        {
            int half = side / 2;
            IntVec3 min = new IntVec3(center.x - half, 0, center.z - half);
            ThingDef wall = ThingDefOf.Wall;
            ThingDef door = ThingDefOf.Door;
            ThingDef stuff = ShelterStuff(map, side <= 7 ? 25 : side <= 9 ? 40 : 55);
            if (wall == null || door == null || stuff == null) return false;

            List<IntVec3> wallCells = new List<IntVec3>();
            IntVec3 doorCell = IntVec3.Invalid;
            for (int x = 0; x < side; x++)
            {
                for (int z = 0; z < side; z++)
                {
                    IntVec3 c = new IntVec3(min.x + x, 0, min.z + z);
                    if (!c.InBounds(map) || c.Fogged(map)) return false;
                    bool edge = x == 0 || z == 0 || x == side - 1 || z == side - 1;
                    if (!Site.GoodGround(map, c)) return false;
                    if (!edge)
                    {
                        if (c.GetEdifice(map) != null) return false;
                        continue;
                    }
                    if (map.zoneManager.ZoneAt(c) != null) return false;
                    if (c.GetEdifice(map) != null) return false;
                    foreach (Thing t in map.thingGrid.ThingsAt(c))
                        if (!t.def.CanOverlapZones) return false;
                    if (x == side / 2 && z == 0) doorCell = c;
                    else wallCells.Add(c);
                }
            }
            if (wallCells.Count < 8 || !doorCell.IsValid) return false;

            // Сначала проверяем всё, потом строим — иначе останутся рваные стены.
            foreach (IntVec3 c in wallCells)
                if (!GenConstruct.CanPlaceBlueprintAt(wall, c, Rot4.North, map, false, null, null, stuff).Accepted) return false;
            if (!GenConstruct.CanPlaceBlueprintAt(door, doorCell, Rot4.North, map, false, null, null, stuff).Accepted) return false;

            foreach (IntVec3 c in wallCells)
                GenConstruct.PlaceBlueprintForBuild(wall, c, map, Rot4.North, Faction.OfPlayer, stuff);
            GenConstruct.PlaceBlueprintForBuild(door, doorCell, map, Rot4.North, Faction.OfPlayer, stuff);
            Mind.Think("быт", "Заложил комнату " + side + "×" + side + " у базы: стены, дверь и крыша. Внутри будут кровати и плита, еда перестанет портиться под дождём.");
            return true;
        }

        /// <summary>Материал стен: сначала камень (не горит), потом дерево; запас не тратим под ноль.</summary>
        private static ThingDef ShelterStuff(Map map, int need)
        {
            string[] stone = { "BlocksGranite", "BlocksLimestone", "BlocksSlate", "BlocksSandstone", "BlocksMarble" };
            foreach (string n in stone)
            {
                ThingDef d = T.Def(n);
                string why;
                if (d != null && Resources.CanSpend(map, d, need, 1.0f, out why)) return d;
            }
            string w;
            if (Resources.CanSpend(map, ThingDefOf.WoodLog, need, 1.0f, out w)) return ThingDefOf.WoodLog;
            return null;
        }

        /// <summary>Когда стены готовы — накрываем комнату крышей (ванильная крыша бесплатна, нужна только работа).</summary>
        private static bool RoofIfEnclosed(Map map, IntVec3 center, int side)
        {
            int half = side / 2;
            IntVec3 min = new IntVec3(center.x - half, 0, center.z - half);
            for (int x = 0; x < side; x++)
            {
                for (int z = 0; z < side; z++)
                {
                    IntVec3 c = new IntVec3(min.x + x, 0, min.z + z);
                    if (!c.InBounds(map)) return false;
                    bool edge = x == 0 || z == 0 || x == side - 1 || z == side - 1;
                    if (!edge) continue;
                    if (c.GetEdifice(map) == null) return false;
                }
            }
            bool changed = false;
            for (int x = 1; x < side - 1; x++)
            {
                for (int z = 1; z < side - 1; z++)
                {
                    IntVec3 c = new IntVec3(min.x + x, 0, min.z + z);
                    if (!c.InBounds(map) || c.Roofed(map)) continue;
                    map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                    changed = true;
                }
            }
            if (changed) Mind.Think("быт", "Стены готовы — накрыл комнату крышей. Теперь внутри можно ставить кровати и плиту.");
            return changed;
        }

        private static void Equip(Map map)
        {
            try
            {
                foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
                {
                    if (p.Downed || p.Drafted || p.equipment == null || p.equipment.Primary != null) continue;
                    if (p.WorkTagIsDisabled(WorkTags.Violent)) continue;
                    if (p.CurJobDef == JobDefOf.Equip) continue;

                    Thing best = null; float score = -1f;
                    foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon))
                    {
                        if (t == null || !t.Spawned || t.Destroyed || t.IsForbidden(Faction.OfPlayer)) continue;
                        if (t.def == null || !t.def.IsWeapon) continue;
                        if (!p.CanReserve(t)) continue;
                        float v = t.MarketValue + (t.def.IsRangedWeapon ? 300f : 0f);
                        if (v <= score) continue;
                        score = v; best = t;
                    }
                    if (best == null) continue;
                    p.jobs.StartJob(new Job(JobDefOf.Equip, best), JobCondition.InterruptForced);
                    Mind.Think("оборона", p.LabelShort + " берёт «" + best.def.label + "»: без оружия колонию не защитить.");
                }
            }
            catch (Exception e) { T.Warn("Экипировка: " + e.Message); }
        }
    }
}
