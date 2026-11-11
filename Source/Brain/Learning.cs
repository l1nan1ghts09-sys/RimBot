using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldBot.TradeEmpire
{
    public enum MetricKind { Food, Mood, Health, Defense, Power, Research, Wealth, Housing, Production, Population, General }

    /// <summary>Ручка основного бота, которую можно настроить и которой можно учиться.</summary>
    public class Knob
    {
        public string field;
        public object[] arms;
        public MetricKind metric;
        public int window;
        public string title;
        public bool safe;          // можно пробовать без большого риска
        public int prior;          // вариант по умолчанию, если опыта нет

        public Knob(string field, MetricKind metric, int window, string title, bool safe, int prior, params object[] arms)
        {
            this.field = field; this.metric = metric; this.window = window;
            this.title = title; this.safe = safe; this.prior = prior; this.arms = arms;
        }

        public string ArmText(int i)
        {
            if (arms == null || arms.Length == 0) return "?";
            if (i < 0 || i >= arms.Length) i = 0;
            object v = arms[i];
            if (v is bool) return (bool)v ? "вкл" : "выкл";
            if (v is float) return ((float)v).ToString("0.00", CultureInfo.InvariantCulture);
            return v.ToString();
        }
    }

    /// <summary>Все ручки, которые бот трогает сам. Порядок вариантов фиксирован: он входит в ключ обучения.</summary>
    public static class KnobBank
    {
        public static readonly Knob[] All =
        {
            new Knob("retreatStrengthRatio", MetricKind.Defense, 90000, "Порог отступления", true, 1, 0.2f, 0.35f, 0.5f, 0.75f),
            new Knob("defenseRadius", MetricKind.Defense, 90000, "Радиус обороны", true, 1, 8, 12, 18, 24),
            new Knob("wallThickness", MetricKind.Defense, 180000, "Толщина стен", true, 1, 1, 2, 3),
            new Knob("useKillbox", MetricKind.Defense, 180000, "Киллбокс", false, 0, false, true),
            new Knob("allowTraps", MetricKind.Defense, 90000, "Ловушки", false, 0, true, false),
            new Knob("allowTurretBuilding", MetricKind.Defense, 120000, "Турели", false, 0, true, false),
            new Knob("minFoodDays", MetricKind.Food, 60000, "Запас еды, дней", true, 1, 8, 12, 16, 22),
            new Knob("minMedicineCount", MetricKind.Health, 120000, "Запас лекарств", true, 1, 6, 12, 20, 30),
            new Knob("minSteelCount", MetricKind.Wealth, 120000, "Запас стали", true, 1, 150, 300, 600, 900),
            new Knob("targetColonists", MetricKind.Population, 180000, "Цель населения", true, 1, 6, 9, 12, 16),
            new Knob("autoHunt", MetricKind.Food, 60000, "Авто-охота", false, 0, true, false),
            new Knob("autoCutPlants", MetricKind.Food, 60000, "Авто-рубка растений", false, 0, true, false),
            new Knob("autoGrowing", MetricKind.Food, 120000, "Авто-фермерство", false, 0, true, false),
            new Knob("autoMedical", MetricKind.Health, 120000, "Авто-медицина", false, 0, true, false),
            new Knob("autoCleanHomeArea", MetricKind.Mood, 60000, "Авто-уборка", false, 0, true, false),
            new Knob("autoEquip", MetricKind.Defense, 180000, "Авто-экипировка", false, 0, true, false),
            new Knob("autoJoy", MetricKind.Mood, 120000, "Авто-радость", false, 0, true, false),
            new Knob("autoTame", MetricKind.Mood, 120000, "Приручение", false, 0, true, false),
            new Knob("autoBurnCorpses", MetricKind.Health, 120000, "Сжигание трупов", false, 0, true, false),
            new Knob("autoMine", MetricKind.Wealth, 120000, "Авто-добыча", false, 0, true, false)
        };

        public static Knob Find(string field)
        {
            for (int i = 0; i < All.Length; i++) if (All[i].field == field) return All[i];
            return null;
        }
    }

    public class Stat
    {
        public int n;
        public double sum;
        public double sumSq;

        public double Mean { get { return n > 0 ? sum / n : 0.0; } }
        public double Variance { get { if (n < 2) return 0.0; double m = Mean; return Math.Max(0.0, sumSq / n - m * m); } }

        public void Add(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return;
            n++;
            sum += v;
            sumSq += v * v;
        }

        public double Shrunk(double prior, double strength)
        {
            return (sum + prior * strength) / (n + strength);
        }
    }

    public class LearnedSkill
    {
        public int n;
        public int ok;
        public double gain;

        public float Rate { get { return n > 0 ? (float)((ok + 1.0) / (n + 2.0)) : 0.5f; } }
        public float Gain { get { return n > 0 ? (float)(gain / n) : 0f; } }

        public void Add(bool success, double g)
        {
            if (double.IsNaN(g) || double.IsInfinity(g)) g = 0;
            n++;
            if (success) ok++;
            gain += U.Clamp((float)g, -1f, 1f);
        }
    }

    public class WeightRec
    {
        public float w = 1f;
        public int n;
    }

    public class Lesson
    {
        public string id = "";
        public string text = "";
        public string ctx = "*";
        public GoalId goal = GoalId.None;
        public float bias;
        public float conf = 0.5f;
        public int day;
        public int applied;
    }

    public class RaidRecord
    {
        public int count;
        public float sumInterval;
        public float sumThreat;
        public float sumWealth;
        public int lastDay = -1;
    }

    /// <summary>
    /// Долговременная память бота. Живёт в Config/RimBotMind.txt, поэтому переживает смерть колонии
    /// и следующая игра начинается с накопленным опытом.
    /// </summary>
    public static class Knowledge
    {
        public const int FormatVersion = 2;

        public static readonly Dictionary<string, Stat> Arms = new Dictionary<string, Stat>();
        public static readonly Dictionary<string, LearnedSkill> Skills = new Dictionary<string, LearnedSkill>();
        public static readonly Dictionary<string, WeightRec> GoalWeights = new Dictionary<string, WeightRec>();
        public static readonly Dictionary<string, Stat> Records = new Dictionary<string, Stat>();
        public static readonly List<Lesson> Lessons = new List<Lesson>();
        public static readonly RaidRecord Raids = new RaidRecord();

        public static readonly float[] Meta = { 1.0f, 1.0f, 0.9f, 0.7f, 0.8f, 0.5f };
        public static int MetaN;

        public static int LastRaidDay = -1;
        public static bool Loaded;
        public static bool Dirty;

        public static string Dir { get { return GenFilePaths.ConfigFolderPath; } }
        public static string Path { get { return System.IO.Path.Combine(Dir, "RimBotMind.txt"); } }
        public static string BackupPath { get { return System.IO.Path.Combine(Dir, "RimBotMind.bak.txt"); } }
        public static string JournalPath { get { return System.IO.Path.Combine(Dir, "RimBotJournal.txt"); } }
        public static string LegacyPath { get { return System.IO.Path.Combine(Dir, "RimBotBrain.txt"); } }

        public static void Load()
        {
            if (Loaded) return;
            Loaded = true;
            try
            {
                if (!File.Exists(Path) && File.Exists(LegacyPath)) { LoadLegacy(); return; }
                if (!File.Exists(Path)) return;
                string[] lines = File.ReadAllLines(Path, Encoding.UTF8);
                foreach (string raw in lines)
                {
                    if (string.IsNullOrEmpty(raw) || raw[0] == '#') continue;
                    try { ParseLine(raw); }
                    catch { }
                }
                Trim();
            }
            catch (Exception e) { T.Warn("Память бота не прочитана: " + e.Message); }
        }

        private static void LoadLegacy()
        {
            try
            {
                foreach (string line in File.ReadAllLines(LegacyPath))
                {
                    string[] p = line.Split('|');
                    if (p.Length != 3) continue;
                    int n = U.ParseInt(p[1], 0);
                    float mean = U.ParseFloat(p[2], 0f);
                    if (n <= 0) continue;
                    // старый ключ "поле#вариант" -> новый "поле|вариант|*"
                    string key = p[0].Replace('#', '|') + "|*";
                    Stat st = GetStat(Arms, key);
                    // старые награды были в «богатство/1000 минус штрафы» — приводим к диапазону новых
                    float scaled = U.Clamp((float)mean / 10f, -1f, 1f);
                    st.n = n;
                    st.sum = scaled * n;
                    st.sumSq = scaled * scaled * n;
                }
                Dirty = true;
                T.Info("Старое обучение перенесено в новый формат (" + Arms.Count + " записей).");
            }
            catch (Exception e) { T.Warn("Старая память не перенесена: " + e.Message); }
        }

        private static void ParseLine(string raw)
        {
            string[] p = raw.Split('|');
            if (p.Length < 2) return;
            switch (p[0])
            {
                case "arm":
                    if (p.Length >= 6) { Stat a = GetStat(Arms, p[1] + "|" + p[2] + "|" + p[3]); a.n = U.ParseInt(p[4], 0); a.sum = U.ParseFloat(p[5], 0f); if (p.Length > 6) a.sumSq = U.ParseFloat(p[6], 0f); }
                    break;
                case "skill":
                    if (p.Length >= 6)
                    {
                        LearnedSkill s = GetSkill(p[1] + "|" + p[2]);
                        s.n = U.ParseInt(p[3], 0); s.ok = U.ParseInt(p[4], 0); s.gain = U.ParseFloat(p[5], 0f);
                    }
                    break;
                case "goal":
                    if (p.Length >= 5)
                    {
                        WeightRec wr = GetWeight(p[1] + "|" + p[2]);
                        wr.w = U.ParseFloat(p[3], 1f); wr.n = U.ParseInt(p[4], 0);
                    }
                    break;
                case "rec":
                    if (p.Length >= 5) { Stat r = GetStat(Records, p[1].Replace('~', '|')); r.n = U.ParseInt(p[2], 0); r.sum = U.ParseFloat(p[3], 0f); r.sumSq = U.ParseFloat(p[4], 0f); }
                    break;
                case "meta":
                    if (p.Length >= 3) { int i = U.ParseInt(p[1], -1); if (i >= 0 && i < Meta.Length) Meta[i] = U.ParseFloat(p[2], Meta[i]); }
                    break;
                case "metan":
                    if (p.Length >= 2) MetaN = U.ParseInt(p[1], 0);
                    break;
                case "raid":
                    if (p.Length >= 7)
                    {
                        Raids.count = U.ParseInt(p[1], 0);
                        Raids.sumInterval = U.ParseFloat(p[2], 0f);
                        Raids.sumThreat = U.ParseFloat(p[3], 0f);
                        Raids.sumWealth = U.ParseFloat(p[4], 0f);
                        Raids.lastDay = U.ParseInt(p[5], -1);
                        LastRaidDay = U.ParseInt(p[6], -1);
                    }
                    break;
                case "lesson":
                    if (p.Length >= 8)
                    {
                        Lesson l = new Lesson();
                        l.day = U.ParseInt(p[1], 0);
                        l.conf = U.ParseFloat(p[2], 0.5f);
                        l.goal = ParseGoal(p[3]);
                        l.bias = U.ParseFloat(p[4], 0f);
                        l.ctx = p[5];
                        l.id = p[6];
                        l.text = string.Join("|", p.Skip(7).ToArray());
                        Lessons.Add(l);
                    }
                    break;
                case "lastraid":
                    if (p.Length >= 2) LastRaidDay = U.ParseInt(p[1], -1);
                    break;
            }
        }

        private static GoalId ParseGoal(string s)
        {
            try { return (GoalId)Enum.Parse(typeof(GoalId), s); } catch { return GoalId.None; }
        }

        public static void Save(bool force)
        {
            if (!Dirty && !force) return;
            try
            {
                Directory.CreateDirectory(Dir);
                List<string> lines = new List<string>();
                lines.Add("# RimWorldBot mind v" + FormatVersion + " — обучение между играми");
                foreach (var kv in Arms)
                {
                    Stat a = kv.Value;
                    lines.Add("arm|" + kv.Key + "|" + a.n + "|" + a.sum.ToString("R", CultureInfo.InvariantCulture) + "|" + a.sumSq.ToString("R", CultureInfo.InvariantCulture));
                }
                foreach (var kv in Skills)
                    lines.Add("skill|" + kv.Key + "|" + kv.Value.n + "|" + kv.Value.ok + "|" + kv.Value.gain.ToString("R", CultureInfo.InvariantCulture));
                foreach (var kv in GoalWeights)
                    lines.Add("goal|" + kv.Key + "|" + kv.Value.w.ToString("R", CultureInfo.InvariantCulture) + "|" + kv.Value.n);
                foreach (var kv in Records)
                    lines.Add("rec|" + kv.Key.Replace('|', '~') + "|" + kv.Value.n + "|" + kv.Value.sum.ToString("R", CultureInfo.InvariantCulture) + "|" + kv.Value.sumSq.ToString("R", CultureInfo.InvariantCulture));
                for (int i = 0; i < Meta.Length; i++) lines.Add("meta|" + i + "|" + Meta[i].ToString("R", CultureInfo.InvariantCulture));
                lines.Add("metan|" + MetaN);
                lines.Add("raid|" + Raids.count + "|" + Raids.sumInterval.ToString("R", CultureInfo.InvariantCulture) + "|"
                    + Raids.sumThreat.ToString("R", CultureInfo.InvariantCulture) + "|" + Raids.sumWealth.ToString("R", CultureInfo.InvariantCulture)
                    + "|" + Raids.lastDay + "|" + LastRaidDay);
                lines.Add("lastraid|" + LastRaidDay);
                foreach (Lesson l in Lessons)
                    lines.Add("lesson|" + l.day + "|" + l.conf.ToString("R", CultureInfo.InvariantCulture) + "|" + l.goal
                        + "|" + l.bias.ToString("R", CultureInfo.InvariantCulture) + "|" + l.ctx + "|" + l.id + "|" + l.text.Replace("\n", " ").Replace("|", "/"));

                string tmp = Path + ".tmp";
                File.WriteAllLines(tmp, lines.ToArray(), Encoding.UTF8);
                try
                {
                    if (File.Exists(Path)) File.Replace(tmp, Path, BackupPath, true);
                    else File.Move(tmp, Path);
                }
                catch
                {
                    File.Copy(tmp, Path, true);
                    try { File.Delete(tmp); } catch { }
                }
                Dirty = false;
                try { lastSave = Find.TickManager != null ? Find.TickManager.TicksGame : 0; } catch { lastSave = 0; }
            }
            catch (Exception e) { T.Warn("Память бота не записана: " + e.Message); }
        }

        public static void SaveIfDirty(int minIntervalTicks)
        {
            int now = 0;
            try { now = Find.TickManager.TicksGame; } catch { }
            if (!Dirty) return;
            if (now - lastSave < minIntervalTicks) return;
            lastSave = now;
            Save(false);
        }
        private static int lastSave = -999999;

        public static void Journal(string text)
        {
            try
            {
                int tick = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
                Directory.CreateDirectory(Dir);
                File.AppendAllText(JournalPath, "[день " + (tick / 60000) + ", " + DateTime.Now.ToString("HH:mm:ss") + "] " + text + Environment.NewLine);
            }
            catch { }
            T.Info(text, "решение");
        }

        public static Stat GetStat(Dictionary<string, Stat> dict, string key)
        {
            Stat s;
            if (!dict.TryGetValue(key, out s)) { s = new Stat(); dict[key] = s; }
            return s;
        }

        public static LearnedSkill GetSkill(string key)
        {
            LearnedSkill s;
            if (!Skills.TryGetValue(key, out s)) { s = new LearnedSkill(); Skills[key] = s; }
            return s;
        }

        public static WeightRec GetWeight(string key)
        {
            WeightRec w;
            if (!GoalWeights.TryGetValue(key, out w)) { w = new WeightRec(); GoalWeights[key] = w; }
            return w;
        }

        public static void ObserveArm(string field, int arm, string ctx, double reward)
        {
            GetStat(Arms, field + "|" + arm + "|" + ctx).Add(reward);
            Dirty = true;
        }

        public static void ObserveSkill(string skill, string ctx, bool ok, double gain)
        {
            GetSkill(skill + "|" + ctx).Add(ok, gain);
            GetSkill(skill + "|*").Add(ok, gain);
            Dirty = true;
        }

        public static double ArmMean(string field, int arm, string ctx, double prior)
        {
            Stat global = SumArm(field, arm);
            Stat local = FindStat(field + "|" + arm + "|" + ctx);
            double globalMean = global.n > 0 ? global.Mean : prior;
            double localMean = local != null ? local.Shrunk(globalMean, 2.5) : globalMean;
            // если в этом контексте пусто, но есть похожие (тот же этап/угроза) — тоже учитываем
            if (local == null || local.n == 0)
            {
                Stat prefix = SumArmPrefix(field, arm, ctx.Length >= 2 ? ctx.Substring(0, 2) : ctx);
                if (prefix.n > 0) localMean = (localMean * 2.0 + prefix.Mean) / 3.0;
            }
            return localMean;
        }

        public static int ArmSamples(string field, int arm, string ctx)
        {
            Stat local = FindStat(field + "|" + arm + "|" + ctx);
            if (local != null) return local.n;
            Stat global = SumArm(field, arm);
            return global.n;
        }

        private static Stat FindStat(string key)
        {
            Stat s;
            return Arms.TryGetValue(key, out s) ? s : null;
        }

        private static Stat SumArm(string field, int arm)
        {
            Stat total = new Stat();
            string prefix = field + "|" + arm + "|";
            foreach (var kv in Arms)
            {
                if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                total.n += kv.Value.n;
                total.sum += kv.Value.sum;
                total.sumSq += kv.Value.sumSq;
            }
            return total;
        }

        private static Stat SumArmPrefix(string field, int arm, string ctxPrefix)
        {
            Stat total = new Stat();
            foreach (var kv in Arms)
            {
                string p = field + "|" + arm + "|";
                if (!kv.Key.StartsWith(p, StringComparison.Ordinal)) continue;
                string ctx = kv.Key.Substring(p.Length);
                if (!ctx.StartsWith(ctxPrefix, StringComparison.Ordinal)) continue;
                total.n += kv.Value.n;
                total.sum += kv.Value.sum;
                total.sumSq += kv.Value.sumSq;
            }
            return total;
        }

        public static float GoalWeight(GoalId id, string goalCtx, float fallback)
        {
            WeightRec exact;
            if (GoalWeights.TryGetValue(id + "|" + goalCtx, out exact) && exact.n > 0)
                return U.Clamp((exact.w * exact.n + fallback * 2f) / (exact.n + 2f), 0.4f, 2.2f);
            WeightRec star;
            if (GoalWeights.TryGetValue(id + "|*", out star) && star.n > 0)
                return U.Clamp((star.w * star.n + fallback * 3f) / (star.n + 3f), 0.4f, 2.2f);
            return U.Clamp(fallback, 0.4f, 2.2f);
        }

        public static void LearnGoalWeight(GoalId id, string goalCtx, float delta)
        {
            WeightRec exact = GetWeight(id + "|" + goalCtx);
            float lr = Mathf.Clamp(0.12f - exact.n * 0.004f, 0.03f, 0.12f);
            exact.w = U.Clamp(exact.w + delta * lr, 0.4f, 2.2f);
            exact.n++;
            WeightRec star = GetWeight(id + "|*");
            star.w = U.Clamp(star.w + delta * lr * 0.5f, 0.4f, 2.2f);
            star.n++;
            Dirty = true;
        }

        public static float LessonBias(GoalId id, string ctx)
        {
            float sum = 0f;
            foreach (Lesson l in Lessons)
            {
                if (l.goal != id || Math.Abs(l.bias) < 0.001f) continue;
                if (l.ctx != "*" && !ctx.StartsWith(l.ctx, StringComparison.Ordinal)) continue;
                sum += l.bias * l.conf;
            }
            return U.Clamp(sum, -0.4f, 0.4f);
        }

        public static void AddLesson(string id, string text, float confidence, string ctx, GoalId goal, float bias)
        {
            if (string.IsNullOrEmpty(id)) return;
            Lesson existing = Lessons.FirstOrDefault(l => l.id == id);
            if (existing != null)
            {
                existing.conf = Mathf.Clamp01(existing.conf + (confidence - existing.conf) * 0.25f);
                existing.text = text;
                existing.day = Find.TickManager != null ? Find.TickManager.TicksGame / 60000 : 0;
                Dirty = true;
                return;
            }
            Lessons.Add(new Lesson
            {
                id = id, text = text, conf = Mathf.Clamp01(confidence), ctx = string.IsNullOrEmpty(ctx) ? "*" : ctx,
                goal = goal, bias = bias, day = Find.TickManager != null ? Find.TickManager.TicksGame / 60000 : 0
            });
            if (Lessons.Count > 300) Lessons.RemoveRange(0, 50);
            Dirty = true;
        }

        public static void ObserveSale(string defName, int count)
        {
            if (string.IsNullOrEmpty(defName) || count <= 0) return;
            GetStat(Records, "sold|" + defName).Add(count);
            Dirty = true;
        }

        public static float DemandScore(string defName)
        {
            if (string.IsNullOrEmpty(defName)) return 0f;
            Stat s;
            // сумма проданных единиц: чем больше уходит, тем выше спрос
            return Records.TryGetValue("sold|" + defName, out s) && s.n > 0 ? (float)s.sum : 0f;
        }

        public static void ObserveRaid(ColonyWorld w)
        {
            if (w == null) return;
            if (Raids.lastDay >= 0 && w.day > Raids.lastDay)
            {
                float interval = w.day - Raids.lastDay;
                if (interval > 0f && interval < 120f)
                {
                    Raids.count++;
                    Raids.sumInterval += interval;
                    Raids.sumThreat += w.threatValue;
                    Raids.sumWealth += w.wealth;
                    Dirty = true;
                }
            }
            Raids.lastDay = w.day;
            LastRaidDay = w.day;
            Dirty = true;
        }

        public static float ExpectedRaidInterval
        {
            get { return Raids.count >= 2 ? Mathf.Clamp(Raids.sumInterval / Raids.count, 3f, 60f) : 9f; }
        }

        /// <summary>Прогноз вероятности рейда: давление времени с последнего рейда + богатство как приманка.</summary>
        public static float RaidRisk(ColonyWorld w)
        {
            if (w == null) return 0f;
            float interval = ExpectedRaidInterval;
            float days = w.daysSinceThreat;
            float pressure = 1f - (float)Math.Exp(-days / Mathf.Max(2f, interval));
            float wealthFactor = U.Clamp01(w.wealth / 25000f);
            float popFactor = U.Clamp01(w.pop / 8f);
            float risk = 0.5f * pressure + 0.3f * wealthFactor + 0.2f * popFactor;
            if (w.threat) risk = Mathf.Max(risk, 0.85f);
            return U.Clamp01(risk * (0.85f + (TradeEmpireMod.S != null ? TradeEmpireMod.S.riskTolerance * 0.3f : 0.1f)));
        }

        public static void Reset()
        {
            Arms.Clear(); Skills.Clear(); GoalWeights.Clear(); Records.Clear(); Lessons.Clear();
            Raids.count = 0; Raids.sumInterval = 0; Raids.sumThreat = 0; Raids.sumWealth = 0; Raids.lastDay = -1;
            LastRaidDay = -1;
            for (int i = 0; i < Meta.Length; i++) Meta[i] = 1f;
            MetaN = 0;
            Dirty = true;
            Save(true);
        }

        private static void Trim()
        {
            if (Arms.Count > 20000)
            {
                var drop = Arms.OrderBy(kv => kv.Value.n).Take(Arms.Count - 16000).Select(kv => kv.Key).ToList();
                foreach (string k in drop) Arms.Remove(k);
            }
            if (Skills.Count > 6000)
            {
                var drop = Skills.OrderBy(kv => kv.Value.n).Take(Skills.Count - 4500).Select(kv => kv.Key).ToList();
                foreach (string k in drop) Skills.Remove(k);
            }
            if (Lessons.Count > 300) Lessons.RemoveRange(0, Lessons.Count - 300);
        }
    }

    /// <summary>Что решил сделать бот на сегодня по каждой ручке.</summary>
    public class KnobDecision
    {
        public Knob knob;
        public int arm;
        public bool changed;
        public bool probe;
        public string why = "";
    }

    /// <summary>Отложенная проверка: какой была метрика до вмешательства и что стало потом.</summary>
    public class Probe
    {
        public string field = "";
        public int arm;
        public string ctx = "";
        public int startDay;
        public int startTick;
        public float startMetric;
        public float startGlobal;
        public int startDeaths;
        public string why = "";

        public string Encode()
        {
            return field + ";" + arm + ";" + ctx + ";" + startDay + ";" + startTick + ";"
                + startMetric.ToString("R", CultureInfo.InvariantCulture) + ";"
                + startGlobal.ToString("R", CultureInfo.InvariantCulture) + ";" + startDeaths + ";" + why.Replace(";", ",").Replace("\n", " ");
        }

        public static Probe Decode(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            string[] p = s.Split(';');
            if (p.Length < 8) return null;
            try
            {
                return new Probe
                {
                    field = p[0], arm = U.ParseInt(p[1], 0), ctx = p[2],
                    startDay = U.ParseInt(p[3], 0), startTick = U.ParseInt(p[4], 0),
                    startMetric = U.ParseFloat(p[5], 0f), startGlobal = U.ParseFloat(p[6], 0f),
                    startDeaths = U.ParseInt(p[7], 0), why = p.Length > 8 ? p[8] : ""
                };
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// Обучение: каждый день выбирает вариант одной ручки для проверки (UCB), остальные ставит
    /// на лучшие известные; через окно оценивает эффект по метрике и запоминает.
    /// Плюс учит веса целей и «что в колонии важнее всего».
    /// </summary>
    public static class Learning
    {
        private static float[] lastFeatures;
        private static int lastMetaDay = -1;
        private static int lastProbeDay = -1;
        private static int lastDeaths;

        public static bool SafeToProbe(ColonyWorld w)
        {
            if (w == null || TradeEmpireMod.S == null) return false;
            if (!TradeEmpireMod.S.experiments) return false;
            if (!TradeEmpireMod.S.learning) return false;
            if (w.pop == 0 || w.threat || w.downed > 0 || w.mental > 0) return false;
            if (w.foodDays < 3f || w.healthScore < 0.75f) return false;
            if (w.day < 2) return false;
            return true;
        }

        public static float MetricValue(ColonyWorld w, MetricKind kind)
        {
            if (w == null) return 0f;
            switch (kind)
            {
                case MetricKind.Food: return w.foodDays;
                case MetricKind.Mood: return w.moodAvg * 10f;
                case MetricKind.Health: return w.healthScore * 10f - w.downed * 2f;
                case MetricKind.Defense: return -(w.downed * 2f + w.bleeding * 0.5f) + (w.threat ? -w.threatRatio * 2f : 0f);
                case MetricKind.Power: return -w.powerDeficit / 100f;
                case MetricKind.Research: return w.researchDone * 3f + (w.researchCurrent != "—" ? 1f : 0f);
                case MetricKind.Wealth: return w.wealth / 1000f;
                case MetricKind.Housing: return w.beds;
                case MetricKind.Production: return w.bills;
                case MetricKind.Population: return w.pop * 3f + w.moodAvg;
                default: return w.foodDays + w.wealth / 1000f + w.healthScore * 5f + w.moodAvg * 3f - w.downed * 2f;
            }
        }

        private static float NormalizeGain(MetricKind kind, float delta)
        {
            switch (kind)
            {
                case MetricKind.Food: return U.Clamp(delta / 4f, -1f, 1f);
                case MetricKind.Mood: return U.Clamp(delta / 2f, -1f, 1f);
                case MetricKind.Health: return U.Clamp(delta / 2f, -1f, 1f);
                case MetricKind.Defense: return U.Clamp(delta / 3f, -1f, 1f);
                case MetricKind.Power: return U.Clamp(delta / 2f, -1f, 1f);
                case MetricKind.Research: return U.Clamp(delta / 3f, -1f, 1f);
                case MetricKind.Wealth: return U.Clamp(delta / 3f, -1f, 1f);
                case MetricKind.Housing: return U.Clamp(delta / 3f, -1f, 1f);
                case MetricKind.Production: return U.Clamp(delta / 5f, -1f, 1f);
                case MetricKind.Population: return U.Clamp(delta / 4f, -1f, 1f);
                default: return U.Clamp(delta / 5f, -1f, 1f);
            }
        }

        /// <summary>Дневная оценка состояния колонии (0..1). Используется как общий сигнал обучения.</summary>
        public static float DayScore(ColonyWorld w)
        {
            if (w == null) return 0f;
            if (w.pop == 0) return 0f;
            float food = U.Clamp01(w.foodDays / Mathf.Max(4f, w.foodTargetDays));
            float health = U.Clamp01(w.healthScore);
            float mood = U.Clamp01(w.moodAvg);
            float safety = U.Clamp01(1f - w.threatRatio);
            float growth = U.Clamp01(0.5f + w.wealthTrend / 600f);
            float science = U.Clamp01(w.researchDone / 25f + (w.researchAvailable > 0 ? 0.1f : 0f));
            float score = food * 0.30f + health * 0.25f + safety * 0.20f + mood * 0.15f + growth * 0.05f + science * 0.05f;
            return U.Clamp01(score);
        }

        public static float[] Features(ColonyWorld w)
        {
            if (w == null) return new float[Knowledge.Meta.Length];
            return new float[]
            {
                U.Clamp01(w.foodDays / Mathf.Max(4f, w.foodTargetDays)),
                U.Clamp01(w.healthScore),
                U.Clamp01(w.moodAvg),
                U.Clamp01(0.5f + w.wealthTrend / 600f),
                U.Clamp01(1f - w.threatRatio),
                U.Clamp01(w.researchDone / 25f)
            };
        }

        /// <summary>Мета-обучение: какие компоненты на самом деле предсказывают выживание колонии.</summary>
        private static void MetaUpdate(ColonyWorld w, bool survived, bool lostSomeone)
        {
            if (lastFeatures == null) { lastFeatures = Features(w); return; }
            float target = !survived ? 0f : lostSomeone ? 0.25f : 1f;
            float pred = 0f;
            for (int i = 0; i < Knowledge.Meta.Length; i++) pred += Knowledge.Meta[i] * lastFeatures[i];
            pred = 1f / (1f + (float)Math.Exp(-pred));
            float lr = 0.05f;
            for (int i = 0; i < Knowledge.Meta.Length; i++)
            {
                Knowledge.Meta[i] = U.Clamp(Knowledge.Meta[i] + lr * (target - pred) * lastFeatures[i], 0.1f, 2.5f);
            }
            Knowledge.MetaN++;
            Knowledge.Dirty = true;
            lastFeatures = Features(w);
        }

        public static float GoalMultiplier(int featureIndex)
        {
            if (featureIndex < 0 || featureIndex >= Knowledge.Meta.Length) return 1f;
            return U.Clamp(0.7f + Knowledge.Meta[featureIndex] * 0.35f, 0.6f, 1.5f);
        }

        // ------------------------------------------------------------ выбор ручек на день
        public static List<KnobDecision> PlanDay(ColonyWorld w, Dictionary<string, int> currentArms, int rotation, Probe pending, out KnobDecision probe)
        {
            probe = null;
            List<KnobDecision> list = new List<KnobDecision>();
            if (w == null || currentArms == null) return list;
            if (TradeEmpireMod.S == null || !TradeEmpireMod.S.learning) return list;

            List<KnobDecision> decisions = new List<KnobDecision>();
            foreach (Knob k in KnobBank.All)
            {
                int cur;
                if (!currentArms.TryGetValue(k.field, out cur))
                {
                    cur = MatchCurrent(k);
                    currentArms[k.field] = cur;
                }
                int best = BestArm(k, w.ctx, cur, out string bestWhy);
                KnobDecision d = new KnobDecision { knob = k, arm = cur, changed = false, why = "" };

                bool switchArm = best != cur;
                if (switchArm && !k.safe)
                {
                    // subsystems: меняем только при явном перевесе и спокойной обстановке
                    double curMean = Knowledge.ArmMean(k.field, cur, w.ctx, 0.5);
                    double bestMean = Knowledge.ArmMean(k.field, best, w.ctx, 0.5);
                    int samples = Knowledge.ArmSamples(k.field, best, w.ctx);
                    switchArm = samples >= 3 && bestMean - curMean > 0.12 && SafeToProbe(w);
                }
                if (switchArm)
                {
                    d.arm = best;
                    d.why = bestWhy;
                }
                // идущую проверку не сбиваем: её вариант должен доработать до конца окна
                if (pending != null && pending.field == k.field)
                {
                    d.arm = U.ClampInt(pending.arm, 0, Math.Max(0, k.arms.Length - 1));
                    d.why = "идёт проверка, вариант не меняю";
                }
                decisions.Add(d);
            }

            // выбор одной ручки для эксперимента
            if (SafeToProbe(w) && pending == null && w.day != lastProbeDay)
            {
                Knob pick = ChooseProbeKnob(w, currentArms, rotation);
                if (pick != null)
                {
                    int cur = currentArms[pick.field];
                    int arm = UcbArm(pick, w.ctx, cur);
                    KnobDecision pd = decisions.FirstOrDefault(x => x.knob == pick);
                    if (pd != null)
                    {
                        pd.arm = arm;
                        pd.probe = true;
                        pd.why = "проверяю вариант «" + pick.ArmText(arm) + "» (было «" + pick.ArmText(cur) + "»)"
                            + (Knowledge.ArmSamples(pick.field, arm, w.ctx) == 0 ? ", новых данных пока нет" : ", опыт " + Knowledge.ArmSamples(pick.field, arm, w.ctx));
                        probe = pd;
                        lastProbeDay = w.day;
                    }
                }
            }

            // применяем
            foreach (KnobDecision d in decisions)
            {
                int cur = currentArms[d.knob.field];
                if (d.arm != cur)
                {
                    bool applied = BotBridge.SetBotSetting(d.knob.field, d.knob.arms[d.arm]);
                    if (applied)
                    {
                        currentArms[d.knob.field] = d.arm;
                        d.changed = true;
                    }
                    else
                    {
                        d.arm = cur;
                        if (d.probe) { d.probe = false; probe = null; }
                    }
                }
                if (d.changed || d.probe) list.Add(d);
            }
            return list;
        }

        private static int MatchCurrent(Knob k)
        {
            object cur = BotBridge.GetBotSetting(k.field);
            if (cur != null)
            {
                string s = cur.ToString();
                for (int i = 0; i < k.arms.Length; i++)
                {
                    if (k.arms[i] != null && k.arms[i].ToString() == s) return i;
                }
            }
            return U.ClampInt(k.prior, 0, Math.Max(0, k.arms.Length - 1));
        }

        private static int BestArm(Knob k, string ctx, int current, out string why)
        {
            why = "";
            int best = current;
            double bestMean = double.MinValue;
            double prior = 0.5;
            for (int i = 0; i < k.arms.Length; i++)
            {
                double m = Knowledge.ArmMean(k.field, i, ctx, prior);
                if (m > bestMean) { bestMean = m; best = i; }
            }
            double curMean = Knowledge.ArmMean(k.field, current, ctx, prior);
            int n = Knowledge.ArmSamples(k.field, best, ctx);
            why = "лучший известный вариант «" + k.ArmText(best) + "» (оценка " + U.F((float)bestMean, 2) + ", опытов " + n + ")";
            if (Math.Abs(bestMean - curMean) < 0.04) return current;
            return best;
        }

        private static Knob ChooseProbeKnob(ColonyWorld w, Dictionary<string, int> arms, int rotation)
        {
            List<Knob> candidates = new List<Knob>();
            foreach (Knob k in KnobBank.All)
            {
                if (!k.safe) continue;
                switch (k.metric)
                {
                    case MetricKind.Food:
                        if (w.foodDays < w.foodTargetDays + 1f) candidates.Add(k);
                        break;
                    case MetricKind.Health:
                        if (w.healthUrgency > 0.05f || w.medicine < 12) candidates.Add(k);
                        break;
                    case MetricKind.Defense:
                        if (w.threatPressure > 0.45f || w.turrets < 1) candidates.Add(k);
                        break;
                    case MetricKind.Mood:
                        if (w.moodUrgency > 0.1f) candidates.Add(k);
                        break;
                    case MetricKind.Power:
                        if (w.powerDeficit > 0f) candidates.Add(k);
                        break;
                    default:
                        candidates.Add(k);
                        break;
                }
            }
            if (candidates.Count == 0) candidates.AddRange(KnobBank.All);
            // наименее изученная ручка в этом контексте; при равенстве — ротация
            candidates = candidates.OrderBy(k =>
            {
                int sum = 0;
                for (int i = 0; i < k.arms.Length; i++) sum += Knowledge.ArmSamples(k.field, i, w.ctx);
                return sum;
            }).ToList();
            int idx = Math.Abs(rotation) % Math.Min(3, candidates.Count);
            return candidates[idx];
        }

        private static int UcbArm(Knob k, string ctx, int current)
        {
            float explore = 0.15f + 0.45f * (TradeEmpireMod.S != null ? TradeEmpireMod.S.riskTolerance : 0.35f);
            double total = 1.0;
            for (int i = 0; i < k.arms.Length; i++) total += Knowledge.ArmSamples(k.field, i, ctx);

            // сначала пробуем неизведанные варианты
            for (int i = 0; i < k.arms.Length; i++)
                if (Knowledge.ArmSamples(k.field, i, ctx) == 0) return i;

            int pick = current;
            double best = double.MinValue;
            for (int i = 0; i < k.arms.Length; i++)
            {
                int n = Knowledge.ArmSamples(k.field, i, ctx);
                double mean = Knowledge.ArmMean(k.field, i, ctx, 0.5);
                double score = mean + explore * Math.Sqrt(Math.Log(total + 1.0) / (n + 1.0));
                if (score > best) { best = score; pick = i; }
            }
            return pick;
        }

        // ------------------------------------------------------------ итоги дня и эксперимента
        public static void DailyReview(ColonyWorld w, ref Probe probe, Dictionary<string, int> currentArms, int deathsTotal)
        {
            if (w == null) return;
            if (lastMetaDay < 0) { lastMetaDay = w.day; lastFeatures = Features(w); lastDeaths = deathsTotal; }
            else if (w.day > lastMetaDay)
            {
                bool survived = w.pop > 0;
                bool lost = deathsTotal > lastDeaths;
                if (TradeEmpireMod.S == null || TradeEmpireMod.S.learning) MetaUpdate(w, survived, lost);
                lastMetaDay = w.day;
                lastDeaths = deathsTotal;
            }

            if (probe != null && TradeEmpireMod.S != null && TradeEmpireMod.S.learning)
            {
                int elapsed = w.tick - probe.startTick;
                Knob k = KnobBank.Find(probe.field);
                if (k != null && elapsed >= Math.Max(6000, k.window - 6000))
                {
                    float value = MetricValue(w, k.metric);
                    float gain = NormalizeGain(k.metric, value - probe.startMetric);
                    float globalDelta = DayScore(w) - probe.startGlobal;
                    float deathsPenalty = Mathf.Max(0, deathsTotal - probe.startDeaths) * 0.45f;
                    float reward = U.Clamp(0.75f * gain + 0.35f * globalDelta - deathsPenalty, -1f, 1f);
                    Knowledge.ObserveArm(k.field, probe.arm, probe.ctx, reward);
                    string verdict = reward > 0.08f ? "лучше" : reward < -0.08f ? "хуже" : "без разницы";
                    Mind.Think("обучение", "Проверка «" + k.title + "» = " + k.ArmText(probe.arm) + ": " + verdict
                        + " (награда " + U.F(reward, 2) + ", метрика " + U.Sign(gain) + "). Опыт сохранён.");
                    Knowledge.Journal("ОБУЧЕНИЕ: " + k.field + "=" + k.ArmText(probe.arm) + " в контексте " + probe.ctx
                        + " → награда " + U.F(reward, 2) + " (" + (probe.why.Length > 0 ? probe.why : "плановая проверка") + ").");
                    probe = null;
                }
                else if (elapsed > 0 && k == null) probe = null;
            }

            // раз в неделю — сводка и уроки
            if (w.day > 0 && w.day % 7 == 0 && w.tick % 60000 < 700)
                BuildLessons(w);

            Knowledge.SaveIfDirty(5000);
        }

        public static Probe StartProbe(ColonyWorld w, KnobDecision d, int deathsTotal)
        {
            if (w == null || d == null || !d.probe) return null;
            return new Probe
            {
                field = d.knob.field, arm = d.arm, ctx = w.ctx, startDay = w.day, startTick = w.tick,
                startMetric = MetricValue(w, d.knob.metric), startGlobal = DayScore(w), startDeaths = deathsTotal,
                why = d.why
            };
        }

        public static void BuildLessons(ColonyWorld w)
        {
            if (w == null) return;
            // 1) явно лучшие варианты ручек
            foreach (Knob k in KnobBank.All)
            {
                int best = -1, second = -1;
                double bestMean = double.MinValue, secondMean = double.MinValue;
                for (int i = 0; i < k.arms.Length; i++)
                {
                    int n = Knowledge.ArmSamples(k.field, i, w.ctx);
                    if (n < 3) continue;
                    double m = Knowledge.ArmMean(k.field, i, w.ctx, 0.5);
                    if (m > bestMean) { secondMean = bestMean; second = best; bestMean = m; best = i; }
                    else if (m > secondMean) { secondMean = m; second = i; }
                }
                if (best >= 0 && second >= 0 && bestMean - secondMean > 0.08)
                {
                    Knowledge.AddLesson("knob:" + k.field + ":" + w.ctx,
                        "«" + k.title + "» = " + k.ArmText(best) + " в условиях " + CtxText(w.ctx)
                        + " даёт лучший исход (оценка " + U.F((float)bestMean, 2) + " против " + U.F((float)secondMean, 2) + ").",
                        Mathf.Clamp01(0.5f + (float)(bestMean - secondMean)), w.ctx, GoalId.None, 0f);
                }
            }
            // 2) ненадёжные навыки
            foreach (var kv in Knowledge.Skills)
            {
                if (kv.Key.EndsWith("|*", StringComparison.Ordinal) || kv.Value.n < 4) continue;
                if (kv.Value.Rate < 0.35f)
                {
                    string skill = kv.Key;
                    Knowledge.AddLesson("skill:" + skill,
                        "Навык «" + skill + "» часто не даёт результата (" + U.Pct(kv.Value.Rate) + " успеха из " + kv.Value.n + " попыток) — бот реже его выбирает.",
                        0.6f, "*", GoalId.None, 0f);
                }
            }
            Knowledge.Save(true);
        }

        public static string CtxText(string ctx)
        {
            if (string.IsNullOrEmpty(ctx) || ctx.Length < 4) return "текущих";
            int st = ctx[0] - '0', th = ctx[1] - '0', fd = ctx[2] - '0', pp = ctx[3] - '0';
            string stage = st == 1 ? "ранняя колония" : st == 2 ? "середина" : "поздняя игра";
            string threat = th == 0 ? "спокойно" : th == 2 ? "сильная угроза" : "угроза";
            string food = fd == 0 ? "голод" : fd == 1 ? "мало еды" : fd == 2 ? "норма еды" : "запас еды";
            string pop = pp == 0 ? "1–2 человека" : pp == 1 ? "малая колония" : pp == 2 ? "средняя" : "большая";
            return stage + ", " + threat + ", " + food + ", " + pop;
        }

        public static void ObserveGoal(GoalId id, string ctx, float delta, int days)
        {
            if (id == GoalId.None || days <= 0) return;
            float normalized = U.Clamp(delta / Mathf.Max(1f, days), -1f, 1f);
            Knowledge.LearnGoalWeight(id, ctx, normalized);
            if (Math.Abs(normalized) > 0.15f)
            {
                Mind.Think("обучение", "Цель «" + Goals.Title(id) + "»: итог " + U.Sign(normalized)
                    + " — вес скорректирован (" + U.F(Knowledge.GoalWeight(id, ctx, 1f), 2) + ").");
            }
        }

        public static string Summary()
        {
            int arms = Knowledge.Arms.Values.Sum(a => a.n);
            int skills = Knowledge.Skills.Values.Sum(s => s.n);
            float err = MetaError();
            return "Опыт: " + arms + " проверок настроек, " + skills + " исходов навыков, уроков " + Knowledge.Lessons.Count
                + ", мета-ошибка " + U.F(err, 2) + ", ожидаемый интервал рейдов " + U.F(Knowledge.ExpectedRaidInterval) + " дн.";
        }

        public static float MetaError()
        {
            // грубая оценка неопределённости: чем меньше наблюдений, тем выше
            return Mathf.Clamp01(1f - Knowledge.MetaN / 40f);
        }

        /// <summary>Новая игра: обнуляем только оперативные данные, накопленный опыт остаётся.</summary>
        public static void ResetRuntime()
        {
            lastFeatures = null;
            lastMetaDay = -1;
            lastProbeDay = -1;
            lastDeaths = 0;
        }

        public static void ResetAll()
        {
            Knowledge.Reset();
            Timeline.Clear();
            Skills.ResetRuntime();
            Reflect.ResetRuntime();
            if (BrainComponent.Get() != null) BrainComponent.Get().ClearProbe();
            Knowledge.LastRaidDay = -1;
            lastFeatures = null;
            lastMetaDay = -1;
            lastProbeDay = -1;
            lastDeaths = 0;
        }
    }

    /// <summary>Навык: что бот умеет делать. Надёжность и полезность учатся по факту.</summary>
    public class Skill
    {
        public string id;
        public string title;
        public string category;
        public GoalId goal;
        public int cooldown;
        public int window;
        public MetricKind metric;
        public Func<ColonyWorld, bool> can;
        public Func<ColonyWorld, SkillResult> run;

        public Skill(string id, string title, string category, GoalId goal, int cooldown, int window, MetricKind metric,
            Func<ColonyWorld, bool> can, Func<ColonyWorld, SkillResult> run)
        {
            this.id = id; this.title = title; this.category = category; this.goal = goal;
            this.cooldown = cooldown; this.window = window; this.metric = metric;
            this.can = can; this.run = run;
        }
    }

    public class SkillResult
    {
        public bool ran;
        public bool success;
        public string message = "";
        public MetricKind metric = MetricKind.General;
        public int window;
        public static SkillResult No(string why) { return new SkillResult { ran = false, success = false, message = why }; }
        public static SkillResult Yes(string msg, MetricKind m, int win) { return new SkillResult { ran = true, success = true, message = msg, metric = m, window = win }; }
        public static SkillResult Fail(string msg) { return new SkillResult { ran = true, success = false, message = msg }; }
    }
}
