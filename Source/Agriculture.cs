using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    /// <summary>
    /// Планировщик полей: выбирает участок не по одной «самой плодородной клетке»,
    /// а по оценке всего пятна — плодородие, расстояние до базы и склада, компактность,
    /// свет, укрытие, опасность у края карты и близость к уже существующим грядкам.
    /// Отдельно следит за культурой, блайтом и сбором урожая.
    /// </summary>
    public static class Agriculture
    {
        public class FieldInfo
        {
            public Zone_Growing zone;
            public int cells, sow, ripe, blighted, living;
            public float avgFertility, bestFertility;
            public bool hydro, roofed;
            public ThingDef crop;
        }

        public class Report
        {
            public int zones, cells, sow, ripe, blighted, target;
            public float avgFertility, bestFertility, foodDays;
            public bool winter, toxic, cold;
            public string crop = "—";
            public string line = "Посевы: оцениваю почву и участки.";
            public List<FieldInfo> fields = new List<FieldInfo>();
        }

        private class Cluster
        {
            public List<IntVec3> cells = new List<IntVec3>();
            public float avgFertility, compact, score;
            public bool hydro;
            public IntVec3 seed;
            public string why = "";
        }

        private static int next;
        private static Map lastMap;
        private static int lastTick = -99999;
        private static Report cached;
        private static string line = "Посевы: оцениваю почву и участки.";

        public static Report Last { get { return cached ?? new Report(); } }
        public static string Line(Map map) { return map == null ? line : (map == lastMap ? line : Analyze(map).line); }

        public static void ResetRuntime()
        {
            next = 0;
            lastMap = null;
            lastTick = -99999;
            cached = null;
            line = "Посевы: оцениваю почву и участки.";
        }

        public static void Run(Map map, int tick)
        {
            if (map == null) return;
            TradeSettings s = TradeEmpireMod.S;
            if (s != null && !s.autoFields) { line = "Посевы: выключены в настройках."; return; }
            if (tick <= 0) tick = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            if (tick < next) return;
            next = tick + 2500;

            try
            {
                Report r = Analyze(map);
                if (r.toxic && r.cells == 0)
                {
                    line = "Посевы: токсичные осадки, новые открытые грядки не закладываю — нужен крытый грунт или гидропоника.";
                    return;
                }
                if (r.winter && r.cells == 0 && map.mapTemperature.OutdoorTemp < 1f)
                {
                    line = "Посевы: зима и мороз, открытая грядка не растёт — жду тепла или гидропоники.";
                    return;
                }

                int added = EnsureFields(map, r, r.target, r.foodDays < 5f || r.blighted > 0);
                if (added > 0)
                    Mind.Think("еда", "Расширил посевы на " + added + " кл. " + r.line);
                line = BuildLine(map, r);
            }
            catch (Exception e) { T.Warn("Посевы: " + e.Message); line = "Посевы: ошибка планирования — " + e.Message; }
        }

        /// <summary>Публичный вызов для кризисов и навыков: гарантирует поля под текущую нужду.</summary>
        public static int EnsureFields(Map map, bool emergency)
        {
            if (map == null) return 0;
            Report r = Analyze(map);
            int target = Math.Max(r.target, emergency ? 40 + Math.Max(1, map.mapPawns.FreeColonistsSpawnedCount) * 18 : r.target);
            int added = EnsureFields(map, r, target, emergency);
            line = BuildLine(map, r);
            return added;
        }

        private static int EnsureFields(Map map, Report r, int target, bool emergency)
        {
            int added = 0;
            added += ManageExisting(map, r, target, emergency);

            // Плохие грядки не считаются выполненной целью: лучше заложить новое поле,
            // чем бесконечно расширять гравий у самой карты.
            bool badWeather = r.toxic || (r.winter && SafeTemp(map) < 1f);
            bool canOpen = !badWeather || HasIndoorFarm(map);

            int effective = EffectiveCells(map, r);
            int need = target - effective - added;
            if (need <= 0) return added;
            if (!canOpen) return added;

            // Сначала расширяем лучшую существующую грядку: меньше зон, короче дорога к складу.
            int expand = ExpandExisting(map, r, need, badWeather);
            added += expand;
            need = target - effective - added;
            if (need <= 0) return added;

            Cluster best = BestCluster(map, Mathf.Clamp(need, 8, 40), emergency, badWeather);
            if (best == null || best.cells.Count < 6)
            {
                if (r.cells == 0)
                    Mind.Think("еда", "Не нашёл безопасного компактного участка с подходящим плодородием — пока полагаюсь на охоту и сбор.");
                return added;
            }

            int made = CreateZone(map, best, r);
            added += made;
            if (made > 0) line = "Посевы: " + line;
            return added;
        }

        private static int ManageExisting(Map map, Report r, int target, bool emergency)
        {
            int changed = 0;
            foreach (FieldInfo f in r.fields)
            {
                if (f.zone == null) continue;
                try { f.zone.allowSow = true; } catch { }

                if (f.crop == null || (f.sow >= Math.Max(3, f.cells / 2) && !SameCrop(f.crop, DesiredCrop(map, f, r, emergency))))
                {
                    ThingDef crop;
                    string why;
                    crop = ChooseCrop(map, f.avgFertility, f.hydro, r.foodDays, r.winter, r.toxic, f.blighted, out why);
                    if (crop != null && !SameCrop(f.crop, crop))
                    {
                        try
                        {
                            f.zone.SetPlantDefToGrow(crop);
                            changed++;
                            Mind.Think("еда", "Меняю культуру на «" + crop.label + "»: " + why + " Среднее плодородие "
                                + Soil.Percent(f.avgFertility) + ".");
                        }
                        catch (Exception e) { T.Warn("Смена культуры: " + e.Message); }
                    }
                }

                if (f.blighted > 0) changed += CutBlighted(map, f);
                if (f.ripe > 0) changed += MarkHarvest(map, f);
            }
            return 0; // смена культуры/сбор — не новые клетки
        }

        private static ThingDef DesiredCrop(Map map, FieldInfo f, Report r, bool emergency)
        {
            string why;
            return ChooseCrop(map, f.avgFertility, f.hydro, r.foodDays, r.winter, r.toxic, f.blighted, out why);
        }

        private static int ExpandExisting(Map map, Report r, int need, bool indoorOnly)
        {
            int added = 0;
            foreach (FieldInfo f in r.fields.OrderByDescending(x => x.avgFertility).ThenBy(x => x.cells))
            {
                if (f.zone == null || need <= 0) continue;
                if (indoorOnly && !f.roofed) continue;            // в осадки/мороз открытое поле не расширяем
                if (!f.hydro && f.avgFertility < 0.78f) continue;   // бедную грядку не расширяем
                IntVec3 center = ZoneCenter(f.zone);
                if (center == IntVec3.Invalid || center.DistanceTo(Infrastructure.Anchor(map)) > 42f) continue;
                List<IntVec3> cells = new List<IntVec3>();
                foreach (IntVec3 c in GenRadial.RadialCellsAround(center, 5f, true))
                {
                    if (cells.Count >= need) break;
                    if (map.zoneManager.ZoneAt(c) != null) continue;
                    if (indoorOnly && !c.Roofed(map)) continue;
                    if (!SuitableForFarm(map, c, Soil.MinFertility)) continue;
                    cells.Add(c);
                }
                if (cells.Count < 3) continue;
                foreach (IntVec3 c in cells)
                {
                    try { f.zone.AddCell(c); added++; AddHome(map, c); }
                    catch { }
                }
                need -= added;
                if (added > 0)
                    Mind.Think("еда", "Расширяю существующую грядку на " + added + " кл. рядом с базой — так урожай ближе к складу.");
                break;
            }
            return added;
        }

        /// <summary>Есть ли уже крытое место, где растёт или может расти еда (гидропоника/теплица).</summary>
        private static bool HasIndoorFarm(Map map)
        {
            if (map == null) return false;
            try
            {
                foreach (Zone z in map.zoneManager.AllZones)
                {
                    Zone_Growing g = z as Zone_Growing;
                    if (g == null) continue;
                    foreach (IntVec3 c in g.cells)
                        if (c.Roofed(map) && (IsHydroponic(map, c) || HasSunLamp(map, c))) return true;
                }
                IntVec3 anchor = Infrastructure.Anchor(map);
                foreach (IntVec3 c in GenRadial.RadialCellsAround(anchor, 24f, true))
                {
                    if (!c.InBounds(map) || c.Fogged(map) || !c.Roofed(map)) continue;
                    if (Soil.Fertility(map, c) < 0.70f) continue;
                    if (HasSunLamp(map, c)) return true;
                }
            }
            catch { }
            return false;
        }

        private static int EffectiveCells(Map map, Report r)
        {
            int n = 0;
            foreach (FieldInfo f in r.fields)
                if (f.hydro || f.avgFertility >= 0.78f) n += f.cells;
            return n;
        }

        private static int CreateZone(Map map, Cluster c, Report r)
        {
            Zone_Growing zone = null;
            int added = 0;
            try
            {
                zone = new Zone_Growing(map.zoneManager);
                map.zoneManager.RegisterZone(zone);
                foreach (IntVec3 cell in c.cells)
                {
                    try { zone.AddCell(cell); added++; AddHome(map, cell); }
                    catch { }
                }
                zone.allowSow = true;
                if (added >= 4)
                {
                    ThingDef crop;
                    string why;
                    crop = ChooseCrop(map, c.avgFertility, c.hydro, r.foodDays, r.winter, r.toxic, r.blighted, out why);
                    if (crop != null) zone.SetPlantDefToGrow(crop);
                    c.score = c.avgFertility;
                    Mind.Think("еда", "Заложил грядку " + added + " кл.: " + c.why + ". Культура «"
                        + (crop != null ? crop.label : "посевы") + "» — " + why + ".");
                    return added;
                }
            }
            catch (Exception e) { T.Warn("Новая грядка: " + e.Message); }
            return Math.Max(0, added);
        }

        /// <summary>Помечает больные блайтом растения на срез, чтобы болезнь не расползлась по полю.</summary>
        public static int CutBlighted(Map map)
        {
            if (map == null) return 0;
            int n = 0;
            Report r = Analyze(map);
            foreach (FieldInfo f in r.fields) n += CutBlighted(map, f);
            if (n > 0) Mind.Think("еда", "Блайт: помечено на срез " + n + " больных растений, иначе болезнь перейдёт на соседние.");
            return n;
        }

        private static int CutBlighted(Map map, FieldInfo f)
        {
            int n = 0;
            if (map == null || f == null || f.zone == null) return 0;
            foreach (IntVec3 c in f.zone.cells)
            {
                Plant p = c.GetPlant(map);
                if (p == null || p.Destroyed || !IsBlighted(p)) continue;
                if (map.designationManager.DesignationOn(p, DesignationDefOf.CutPlant) != null) continue;
                try { map.designationManager.AddDesignation(new Designation(p, DesignationDefOf.CutPlant)); n++; }
                catch { }
            }
            return n;
        }

        private static int MarkHarvest(Map map, FieldInfo f)
        {
            int n = 0;
            if (map == null || f == null || f.zone == null) return 0;
            foreach (IntVec3 c in f.zone.cells)
            {
                Plant p = c.GetPlant(map);
                if (p == null || p.Destroyed || !p.HarvestableNow || IsBlighted(p)) continue;
                if (map.designationManager.DesignationOn(p, DesignationDefOf.HarvestPlant) != null) continue;
                try { map.designationManager.AddDesignation(new Designation(p, DesignationDefOf.HarvestPlant)); n++; }
                catch { }
            }
            return n;
        }

        // ============================== анализ полей ==============================

        public static Report Analyze(Map map)
        {
            int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            if (map != null && map == lastMap && cached != null && Math.Abs(now - lastTick) < 500) return cached;

            Report r = new Report();
            r.winter = IsWinter(map);
            r.toxic = HasCondition(map, "toxic");
            r.foodDays = FoodDays(map);
            r.cold = map != null && SafeTemp(map) < 5f;

            if (map != null)
            {
                try
                {
                    foreach (Zone z in map.zoneManager.AllZones)
                    {
                        Zone_Growing g = z as Zone_Growing;
                        if (g == null) continue;
                        FieldInfo f = new FieldInfo { zone = g, crop = CropOf(g) };
                        float sum = 0f, best = 0f;
                        foreach (IntVec3 c in g.cells)
                        {
                            f.cells++;
                            float fert = Soil.Fertility(map, c);
                            sum += fert;
                            if (fert > best) best = fert;
                            if (c.Roofed(map)) f.roofed = true;
                            if (IsHydroponic(map, c)) f.hydro = true;
                            Plant p = c.GetPlant(map);
                            if (p == null || p.Destroyed) { f.sow++; continue; }
                            f.living++;
                            if (IsBlighted(p)) f.blighted++;
                            if (p.HarvestableNow) f.ripe++;
                        }
                        f.avgFertility = f.cells > 0 ? sum / f.cells : 0f;
                        f.bestFertility = best;
                        f.roofed = f.roofed || (f.cells > 0 && g.cells.All(c => c.Roofed(map)));
                        r.fields.Add(f);
                        r.zones++;
                        r.cells += f.cells;
                        r.sow += f.sow;
                        r.ripe += f.ripe;
                        r.blighted += f.blighted;
                        r.avgFertility += f.avgFertility * f.cells;
                        if (f.bestFertility > r.bestFertility) r.bestFertility = f.bestFertility;
                    }
                }
                catch (Exception e) { T.Warn("Анализ грядок: " + e.Message); }
            }

            if (r.cells > 0) r.avgFertility /= r.cells;
            r.target = TargetCells(map, r);
            r.crop = CropLabel(map, r);
            r.line = BuildLine(map, r);
            cached = r;
            lastMap = map;
            lastTick = now;
            line = r.line;
            return r;
        }

        private static int TargetCells(Map map, Report r)
        {
            if (map == null) return 0;
            int pop = Math.Max(1, map.mapPawns.FreeColonistsSpawnedCount);
            float avg = r.avgFertility > 0.01f ? r.avgFertility : Soil.GoodFertility;
            ThingDef crop;
            string why;
            crop = ChooseCrop(map, avg, HasHydro(map), r.foodDays, r.winter, r.toxic, r.blighted, out why);

            float nut = 0.05f, yield = 6f, grow = 6f;
            try
            {
                if (crop != null && crop.plant != null && crop.plant.harvestedThingDef != null)
                {
                    nut = crop.plant.harvestedThingDef.GetStatValueAbstract(StatDefOf.Nutrition);
                    object y = R.Get(crop.plant, "harvestYield"); if (y is float) yield = (float)y;
                    object g = R.Get(crop.plant, "growDays"); if (g is float) grow = (float)g;
                }
            }
            catch { }
            nut = Mathf.Max(0.01f, nut);
            yield = Mathf.Max(1f, yield);
            grow = Mathf.Max(2f, grow);
            float cycle = Mathf.Max(2f, grow / Mathf.Clamp(avg, 0.55f, 1.05f));
            float perCellDay = yield * nut / cycle;
            int need = Mathf.CeilToInt(pop * 2.6f * 1.15f / Mathf.Max(0.02f, perCellDay));
            int floor = 36 + pop * 12;
            int cap = 40 + pop * 30;
            if (r.foodDays > 15f) cap = Mathf.Min(cap, 40 + pop * 18);
            if (r.foodDays < 4f) floor = Mathf.Max(floor, 40 + pop * 16);
            return Mathf.Clamp(Mathf.Max(need, floor), 24, cap);
        }

        private static ThingDef ChooseCrop(Map map, float avg, bool hydro, float foodDays, bool winter,
            bool toxic, int blighted, out string why)
        {
            ThingDef rice = T.Def("Plant_Rice");
            ThingDef corn = T.Def("Plant_Corn");
            ThingDef potato = T.Def("Plant_Potato");
            bool cold = winter || SafeTemp(map) < 6f;

            if (blighted > 0)
            {
                why = "после блайта нужен быстрый и устойчивый рис, чтобы быстрее восстановить запас";
                return rice ?? potato ?? corn;
            }
            if (foodDays < 6f || toxic)
            {
                why = "запас еды низкий: рис созревает быстрее всех и быстрее закрывает голод";
                return rice ?? potato ?? corn;
            }
            if (hydro)
            {
                if (foodDays < 12f) { why = "гидропоника под лампой: рис даёт быстрый оборот"; return rice ?? corn; }
                why = "гидропоника: кукуруза даёт больше питания с клетки при стабильном свете";
                return corn ?? rice;
            }
            if (avg >= Soil.GoodFertility && !cold && foodDays > 8f)
            {
                why = "богатая почва и тёплый сезон: кукуруза выгоднее по питательности и работе";
                return corn ?? rice;
            }
            if (avg < 0.85f || cold)
            {
                why = "почва беднее или холодно: картофель выносливее риса и кукурузы";
                return potato ?? rice ?? corn;
            }
            why = "средняя почва: рис — надёжный баланс скорости и урожая";
            return rice ?? potato ?? corn;
        }

        // ============================== выбор участка ==============================

        private static Cluster BestCluster(Map map, int want, bool emergency, bool indoorOnly)
        {
            if (map == null) return null;
            IntVec3 anchor = Infrastructure.Anchor(map);
            IntVec3 stock = StockCenter(map);
            float radius = Mathf.Clamp(30f + map.mapPawns.FreeColonistsSpawnedCount * 2f, 30f, 48f);
            List<Pawn> hostiles = Hostiles(map);
            List<IntVec3> cells = new List<IntVec3>();
            foreach (IntVec3 c in GenRadial.RadialCellsAround(anchor, radius, true))
            {
                if (!SuitableForFarm(map, c, Soil.MinFertility)) continue;
                if (indoorOnly && !c.Roofed(map)) continue;
                if (NearEdge(map, c, 5)) continue;
                if (NearHostile(c, hostiles, 10f)) continue;
                cells.Add(c);
            }
            if (cells.Count < 6) return null;

            List<IntVec3> seeds = cells
                .OrderByDescending(c => Soil.Fertility(map, c) * 100f - c.DistanceTo(anchor) * 0.55f)
                .Take(70).ToList();

            Cluster best = null;
            foreach (IntVec3 seed in seeds)
            {
                Cluster c = BuildCluster(map, seed, cells, want, anchor, stock);
                if (c == null) continue;
                if (best == null || c.score > best.score) best = c;
                if (best != null && best.score > 125f && best.compact > 0.8f) break;
            }

            if (best != null && best.avgFertility < (emergency ? 0.72f : 0.80f))
            {
                if (best.avgFertility < 0.68f) return null;
                best.why += " (почва ниже идеала — беру только из-за нехватки еды)";
            }
            if (best != null && best.cells.Count > want) best.cells = best.cells.Take(want).ToList();
            return best;
        }

        private static Cluster BuildCluster(Map map, IntVec3 seed, List<IntVec3> all, int want,
            IntVec3 anchor, IntVec3 stock)
        {
            Cluster c = new Cluster { seed = seed };
            c.hydro = IsHydroponic(map, seed);
            List<IntVec3> near = new List<IntVec3>();
            foreach (IntVec3 x in all)
                if (x.DistanceToSquared(seed) <= 42f) near.Add(x);
            if (near.Count < 6) return null;

            near.Sort((a, b) =>
            {
                float sa = Soil.Fertility(map, a) * 100f - a.DistanceTo(seed) * 1.1f - a.DistanceTo(anchor) * 0.18f;
                float sb = Soil.Fertility(map, b) * 100f - b.DistanceTo(seed) * 1.1f - b.DistanceTo(anchor) * 0.18f;
                return sb.CompareTo(sa);
            });
            c.cells = near.Take(want).ToList();
            float sum = 0f;
            HashSet<IntVec3> set = new HashSet<IntVec3>(c.cells);
            int compact = 0;
            foreach (IntVec3 x in c.cells)
            {
                sum += Soil.Fertility(map, x);
                int n = 0;
                foreach (IntVec3 a in GenAdj.CellsAdjacent8Way(new TargetInfo(x, map)))
                    if (set.Contains(a)) n++;
                if (n >= 2) compact++;
            }
            c.avgFertility = sum / Mathf.Max(1, c.cells.Count);
            c.compact = (float)compact / Mathf.Max(1, c.cells.Count);
            int nearExisting = 0;
            foreach (IntVec3 x in c.cells)
                foreach (IntVec3 a in GenAdj.CellsAdjacent8Way(new TargetInfo(x, map)))
                    if (map.zoneManager.ZoneAt(a) is Zone_Growing) { nearExisting++; break; }

            c.score = c.avgFertility * 100f + c.compact * 22f
                - seed.DistanceTo(anchor) * 0.55f - seed.DistanceTo(stock) * 0.25f
                + (c.hydro ? 10f : 0f) + nearExisting * 2.5f;
            c.why = "плодородие " + Soil.Percent(c.avgFertility)
                + ", до базы " + Mathf.RoundToInt(seed.DistanceTo(anchor)) + " кл."
                + ", до склада " + Mathf.RoundToInt(seed.DistanceTo(stock)) + " кл."
                + ", компактность " + Soil.Percent(c.compact)
                + (c.hydro ? ", гидропоника" : "") + (nearExisting > 0 ? ", рядом с другими грядками" : "");
            return c;
        }

        /// <summary>Клетка пригодна для грядки: открытая почва либо гидропоника под лампой.</summary>
        public static bool SuitableForFarm(Map map, IntVec3 c, float minFertility)
        {
            if (map == null || !c.InBounds(map) || c.Fogged(map)) return false;
            if (map.zoneManager.ZoneAt(c) != null) return false;

            bool hydro = IsHydroponic(map, c);
            if (hydro)
            {
                if (c.Roofed(map) && !HasSunLamp(map, c)) return false;
                return true;
            }

            if (c.Roofed(map))
            {
                // Теплица: крыша есть, но под ней горит солнечная лампа — растение получает свет.
                if (!HasSunLamp(map, c)) return false;
                if (Soil.Fertility(map, c) < minFertility) return false;
                if (c.GetEdifice(map) != null) return false;
                foreach (Thing t in map.thingGrid.ThingsAt(c))
                    if (!t.def.CanOverlapZones) return false;
                return true;
            }
            if (Soil.Fertility(map, c) < minFertility) return false;
            if (c.GetEdifice(map) != null) return false;
            foreach (Thing t in map.thingGrid.ThingsAt(c))
                if (!t.def.CanOverlapZones) return false;
            return true;
        }

        private static bool SameCrop(ThingDef a, ThingDef b)
        {
            return a == b || (a != null && b != null && a.defName == b.defName);
        }

        private static ThingDef CropOf(Zone_Growing zone)
        {
            try
            {
                object v = R.Call(zone, "GetPlantDefToGrow");
                if (v is ThingDef) return (ThingDef)v;
                v = R.Get(zone, "plantDefToGrow");
                if (v is ThingDef) return (ThingDef)v;
            }
            catch { }
            return null;
        }

        private static string CropLabel(Map map, Report r)
        {
            if (r.fields.Count > 0)
            {
                FieldInfo f = r.fields.OrderByDescending(x => x.cells).FirstOrDefault();
                if (f != null && f.crop != null) return f.crop.label;
            }
            string why;
            ThingDef d = ChooseCrop(map, r.avgFertility, HasHydro(map), r.foodDays, r.winter, r.toxic, r.blighted, out why);
            return d != null ? d.label : "посевы";
        }

        private static string BuildLine(Map map, Report r)
        {
            if (map == null) return line;
            string state = r.cells == 0 ? "грядок нет" : r.cells + "/" + r.target + " кл.";
            return "Посевы: " + state + ", плодородие " + Soil.Percent(r.avgFertility)
                + ", посеять " + r.sow + ", собрать " + r.ripe + ", блайт " + r.blighted
                + ", культура " + r.crop
                + (r.winter ? ", зима" : "") + (r.toxic ? ", токсичные осадки" : "")
                + ". Участок выбирается по плодородию, расстоянию до склада и компактности.";
        }

        private static float FoodDays(Map map)
        {
            try
            {
                BrainComponent b = BrainComponent.Get();
                if (b != null && b.World != null && b.World.map == map) return b.World.foodDays;
            }
            catch { }
            try { return FoodSafety.Analyze(map).daysFood; } catch { return 0f; }
        }

        private static float SafeTemp(Map map)
        {
            try { return map.mapTemperature.OutdoorTemp; } catch { return 20f; }
        }

        private static IntVec3 ZoneCenter(Zone_Growing zone)
        {
            try
            {
                if (zone != null && zone.cells.Count > 0)
                    return new IntVec3((int)zone.cells.Average(c => c.x), 0, (int)zone.cells.Average(c => c.z));
            }
            catch { }
            return IntVec3.Invalid;
        }

        private static IntVec3 StockCenter(Map map)
        {
            try
            {
                Zone_Stockpile z = map.zoneManager.AllZones.OfType<Zone_Stockpile>()
                    .OrderByDescending(x => x.cells.Count).FirstOrDefault();
                if (z != null && z.cells.Count > 0)
                    return new IntVec3((int)z.cells.Average(c => c.x), 0, (int)z.cells.Average(c => c.z));
            }
            catch { }
            return Infrastructure.Anchor(map);
        }

        private static void AddHome(Map map, IntVec3 c)
        {
            try { if (map.areaManager.Home != null) map.areaManager.Home[c] = true; } catch { }
        }

        private static bool NearEdge(Map map, IntVec3 c, int margin)
        {
            return c.x < margin || c.z < margin || c.x >= map.Size.x - margin || c.z >= map.Size.z - margin;
        }

        private static List<Pawn> Hostiles(Map map)
        {
            List<Pawn> list = new List<Pawn>();
            try
            {
                foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                    if (p != null && p.Spawned && !p.Dead && p.HostileTo(Faction.OfPlayer)) list.Add(p);
            }
            catch { }
            return list;
        }

        private static bool NearHostile(IntVec3 c, List<Pawn> hostiles, float dist)
        {
            for (int i = 0; i < hostiles.Count; i++)
                if (hostiles[i].Position.DistanceTo(c) < dist) return true;
            return false;
        }

        // ============================== среда и культура ==============================

        public static bool IsWinter(Map map)
        {
            if (map == null) return false;
            try
            {
                object s = R.CallStatic("RimWorld.GenLocalDate", "Season", map);
                string name = s != null ? s.ToString().ToLowerInvariant() : "";
                if (name.Contains("winter") || name.Contains("зим")) return true;
            }
            catch { }
            try
            {
                object d = R.CallStatic("RimWorld.GenLocalDate", "DayOfYear", map);
                if (d != null)
                {
                    int day = Convert.ToInt32(d);
                    if (day >= 45 && day < 60) return true;
                }
            }
            catch { }
            return SafeTemp(map) < -8f;
        }

        public static bool HasCondition(Map map, string token)
        {
            if (map == null || string.IsNullOrEmpty(token)) return false;
            try
            {
                foreach (GameCondition gc in map.gameConditionManager.ActiveConditions)
                {
                    if (gc == null || gc.def == null) continue;
                    string dn = (gc.def.defName ?? "").ToLowerInvariant();
                    if (dn.Contains(token)) return true;
                    string label = (gc.def.label ?? "").ToLowerInvariant();
                    if (label.Contains(token)) return true;
                }
            }
            catch { }
            return false;
        }

        public static bool HasHydro(Map map)
        {
            if (map == null) return false;
            try
            {
                foreach (Zone z in map.zoneManager.AllZones)
                {
                    Zone_Growing g = z as Zone_Growing;
                    if (g == null) continue;
                    foreach (IntVec3 c in g.cells) if (IsHydroponic(map, c)) return true;
                }
            }
            catch { }
            return false;
        }

        public static bool IsHydroponic(Map map, IntVec3 c)
        {
            if (map == null || !c.InBounds(map)) return false;
            try
            {
                TerrainDef t = c.GetTerrain(map);
                if (t != null && t.defName != null
                    && t.defName.IndexOf("Hydroponic", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                foreach (Thing thing in c.GetThingList(map))
                {
                    if (thing == null || thing.def == null || thing.def.defName == null) continue;
                    if (thing.def.defName.IndexOf("Hydroponic", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
            catch { }
            return false;
        }

        public static bool HasSunLamp(Map map, IntVec3 c)
        {
            try
            {
                foreach (Building b in map.listerBuildings.allBuildingsColonist)
                {
                    if (b == null || b.def == null || b.def.defName == null) continue;
                    if (b.def.defName.IndexOf("SunLamp", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (b.Position.InHorDistOf(c, 16f)) return true;
                }
            }
            catch { }
            return false;
        }

        private static bool IsBlighted(Plant p)
        {
            try
            {
                object v = R.Get(p, "Blighted");
                if (v is bool) return (bool)v;
            }
            catch { }
            return false;
        }
    }
}
