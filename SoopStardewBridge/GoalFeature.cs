using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.Locations;
using StardewValley.Locations;
using HarmonyLib;
using StardewValley.Menus;

namespace SoopStardewBridge
{
    // ── 공동 목표: 공용 돈을 모아 목표 금액(config.json GoalGold)을 제출한다 ──────────
    // - 화면 오른쪽 위(시계 아래) 아이콘 → 목표 창에서 금액을 입력해 제출 요청
    // - 접속 중인 다른 플레이어가 모두 동의해야 제출된다 (아무도 없으면 바로 제출). 제출한 돈은 돌려받을 수 없다.
    // - 기한(기본 1년차 겨울 28일)이 끝나기 전에 모으면 전체 안내 + 특별한 장소(정상)로 이동할 수 있다.
    // 진행 상황과 투표는 호스트(게임 서버)가 관리하고 state.json에 저장한다.

    internal sealed class GoalStatusMessage
    {
        public bool   Enabled { get; set; }
        public long   Target { get; set; }
        public long   Submitted { get; set; }
        public bool   Achieved { get; set; }
        public bool   Failed { get; set; }
        public string Deadline { get; set; } = "";
        public string PendingId { get; set; } = "";
        public string PendingProposer { get; set; } = "";
        public long   PendingAmount { get; set; }
        public List<long> Voters { get; set; } = new();   // 동의해야 하는 사람
        public List<long> Agreed { get; set; } = new();
    }
    internal sealed class GoalRequestMessage  { }
    internal sealed class GoalProposeMessage  { public long Amount { get; set; } }
    internal sealed class GoalVoteRequestMessage { public string Id { get; set; } = ""; public string Proposer { get; set; } = ""; public long Amount { get; set; } }
    internal sealed class GoalVoteMessage     { public string Id { get; set; } = ""; public bool Agree { get; set; } }
    // 약속의 정상 이동: 한 명이 요청 → 모두에게 묻고 → 모두 '예'면 함께 이동, 한 명이라도 '아니오'면 취소
    internal sealed class GoalTripRequestMessage { }
    internal sealed class GoalTripAskMessage  { public string Id { get; set; } = ""; public string Requester { get; set; } = ""; }
    internal sealed class GoalTripVoteMessage { public string Id { get; set; } = ""; public bool Agree { get; set; } }
    internal sealed class GoalTripGoMessage   { }

    internal sealed partial class ModEntry
    {
        public const string GoalLocationName = "SoopStardewBridge_GoalSummit";
        private const string MsgGoalRequest = "GoalRequest", MsgGoalStatus = "GoalStatus", MsgGoalPropose = "GoalPropose",
                             MsgGoalVoteRequest = "GoalVoteRequest", MsgGoalVote = "GoalVote",
                             MsgGoalTripRequest = "GoalTripRequest", MsgGoalTripAsk = "GoalTripAsk", MsgGoalTripVote = "GoalTripVote", MsgGoalTripGo = "GoalTripGo";
        private const string GoalTripQuestion = "모두 함께 약속의 정상으로 이동할까요?\n(한 명이라도 '아니오'를 누르면 아무도 이동하지 않습니다. 정상 아래쪽 길로 내려가면 원래 있던 곳으로 돌아옵니다)";
        private static readonly TimeSpan GoalVoteTimeout = TimeSpan.FromSeconds(90);

        // 호스트
        private sealed class GoalProposal
        {
            public string Id = Guid.NewGuid().ToString("N");
            public long From; public string FromName = ""; public long Amount;
            public HashSet<long> Voters = new(); public HashSet<long> Agreed = new();
            public DateTime At = DateTime.UtcNow;
        }
        private GoalProposal? goalProposal;
        private GoalProposal? goalTrip;   // 이동 투표 (Amount는 쓰지 않음)
        private long lastGoalTarget = -1;

        // 플레이어 게임
        public GoalStatusMessage? ClientGoal;
        public GoalVoteRequestMessage? ClientGoalVote;   // 내가 답해야 하는 요청
        private DateTime clientGoalAskedAt = DateTime.MinValue;
        private Rectangle goalIconRect;
        private Item? goalIconItem;

        private void InitGoal()
        {
            Helper.Events.Content.AssetRequested  += OnGoalAssetRequested;
            PatchGoalSummit();
            Helper.Events.Multiplayer.ModMessageReceived += OnGoalModMessage;
            Helper.Events.Display.RenderedHud     += OnGoalRenderedHud;
            Helper.Events.Input.ButtonPressed     += OnGoalButtonPressed;
            Helper.Events.GameLoop.UpdateTicked   += OnGoalUpdateTicked;
            Helper.Events.GameLoop.DayEnding      += (_, _) => { if (Context.IsMainPlayer) CheckGoalDeadline(dayEnding: true); };
            Helper.Events.GameLoop.DayStarted     += (_, _) => { if (Context.IsMainPlayer) { EnsureGoalMap(); CheckGoalDeadline(dayEnding: false); BroadcastGoal(); } };
            Helper.Events.GameLoop.ReturnedToTitle += (_, _) => { ClientGoal = null; ClientGoalVote = null; goalProposal = null; };
            try
            {
                ChatCommands.Register(ModManifest.UniqueID + "_goal", (_, _) => OpenGoalMenu(),
                    _ => "공동 목표 창을 엽니다.", aliases: new[] { "목표", "goal" });
                ChatCommands.Register(ModManifest.UniqueID + "_agree", (_, _) => AnswerGoalVote(true),
                    _ => "공동 목표 제출 요청에 동의합니다.", aliases: new[] { "동의" });
                ChatCommands.Register(ModManifest.UniqueID + "_reject", (_, _) => AnswerGoalVote(false),
                    _ => "공동 목표 제출 요청을 거절합니다.", aliases: new[] { "거절" });
            }
            catch (Exception ex) { Monitor.Log($"목표 채팅 명령어 등록 실패: {ex.Message}", LogLevel.Warn); }
        }

        // 특별한 장소 "약속의 정상": assets/goal_summit.tmx (정상 맵을 고친 것: 출구 없음, 정면을 보는 4인용 벤치)
        public const string GoalMapAsset = "Maps/SoopStardewBridge_GoalSummit";
        private void OnGoalAssetRequested(object? sender, AssetRequestedEventArgs e)
        {
            if (e.NameWithoutLocale.IsEquivalentTo(GoalMapAsset))
            {
                e.LoadFromModFile<xTile.Map>("assets/goal_summit.tmx", AssetLoadPriority.Exclusive);
                return;
            }
            if (e.NameWithoutLocale.IsEquivalentTo("Data/ChairTiles"))
            {
                // 4칸 벤치(assets/soop_goal_bench.png 첫 칸): 4명이 화면 쪽(아래)을 보고 앉는다
                e.Edit(asset => asset.AsDictionary<string, string>().Data["soop_goal_bench/0/0"] = "4/1/down/bench/-1/-1/false");
                return;
            }
            if (!e.NameWithoutLocale.IsEquivalentTo("Data/Locations")) return;
            e.Edit(asset =>
            {
                var data = asset.AsDictionary<string, LocationData>().Data;
                data[GoalLocationName] = new LocationData
                {
                    DisplayName = "약속의 정상",
                    DefaultArrivalTile = new Point(10, 20),
                    ExcludeFromNpcPathfinding = true,
                    CanPlantHere = false,
                    CreateOnLoad = new CreateLocationData { MapPath = GoalMapAsset.Replace('/', '\\'), Type = "StardewValley.Locations.Summit", AlwaysActive = false }
                };
            });
        }

        // 정상(Summit) 장소는 완벽함 100%가 아니면 Qi가 나와 "치팅"으로 처리하고, 두 번째 방문부터는 체력을 -1로 만들어 기절시킨다.
        // 약속의 정상에서는 그 부분을 건너뛰고 노을 하늘·바람 등 원래 정상 화면만 보여 준다 (업적은 주지 않는다).
        private static Action<GameLocation>? baseResetLocalState;
        private void PatchGoalSummit()
        {
            try
            {
                var baseMethod = AccessTools.Method(typeof(GameLocation), "resetLocalState");
                baseResetLocalState = AccessTools.MethodDelegate<Action<GameLocation>>(baseMethod, virtualCall: false);
                var harmony = new Harmony(ModManifest.UniqueID + ".goalsummit");
                harmony.Patch(AccessTools.Method(typeof(Summit), "resetLocalState"),
                    prefix: new HarmonyMethod(typeof(ModEntry), nameof(GoalSummitResetPrefix)));
                harmony.Patch(AccessTools.Method(typeof(Background), nameof(Background.draw)),
                    prefix: new HarmonyMethod(typeof(ModEntry), nameof(GoalSkyPrefix)),
                    postfix: new HarmonyMethod(typeof(ModEntry), nameof(GoalSkyPostfix)),
                    finalizer: new HarmonyMethod(typeof(ModEntry), nameof(GoalSkyFinalizer)));
            }
            catch (Exception ex) { Monitor.Log($"약속의 정상 화면 패치 실패: {ex.Message}", LogLevel.Warn); }
        }

        private static bool GoalSummitResetPrefix(Summit __instance)
        {
            if (__instance.NameOrUniqueName != GoalLocationName || baseResetLocalState == null) return true;
            __instance.isShowingEndSlideshow = false;
            __instance.isOutdoors.Value = false;
            baseResetLocalState(__instance);
            Game1.background = new Background(__instance);   // 정상 전용 노을 하늘과 구름
            __instance.temporarySprites.Clear();
            Game1.displayHUD = true;
            Game1.changeMusicTrack("winter_day_ambient", track_interruptable: true, StardewValley.GameData.MusicContext.SubLocation);
            return false;
        }

        // ── 약속의 정상 밤하늘: 항상 밤(별이 뜬 하늘) + 큰 달 + 별똥별 ──
        // 정상 하늘은 시간에 따라 바뀌므로, 하늘을 그리는 동안만 시각을 밤 9시로 보이게 한다 (실제 게임 시간은 그대로).
        private static int realTimeOfDay = -1;
        private static bool AtGoalPlace => Game1.currentLocation?.NameOrUniqueName == GoalLocationName;

        private static void GoalSkyPrefix()
        {
            if (!AtGoalPlace) return;
            realTimeOfDay = Game1.timeOfDay;
            Game1.timeOfDay = 2100;
        }

        private static Exception? GoalSkyFinalizer(Exception? __exception)
        {
            if (realTimeOfDay >= 0) { Game1.timeOfDay = realTimeOfDay; realTimeOfDay = -1; }
            return __exception;
        }

        private sealed class Meteor { public Vector2 Pos; public Vector2 Vel; public float Life, MaxLife, Length; }
        private static readonly List<Meteor> meteors = new();
        private static readonly Random skyRandom = new();
        private static double nextMeteorAt;

        private static void GoalSkyPostfix(SpriteBatch b)
        {
            if (!AtGoalPlace || Game1.viewport.X <= -1000) return;
            int w = Game1.viewport.Width, h = Game1.viewport.Height;
            double now = Game1.currentGameTime?.TotalGameTime.TotalSeconds ?? 0;
            float dt = (float)(Game1.currentGameTime?.ElapsedGameTime.TotalSeconds ?? 0.016);

            // 달: 은은한 빛 + 달 그림 (게임 원래 달 그림을 크게)
            var moonSrc = new Rectangle(642, 834, 43, 44);
            var moonPos = new Vector2(w * 0.74f, h * 0.06f);
            b.Draw(Game1.mouseCursors, moonPos + new Vector2(43 * 2.5f, 44 * 2.5f), moonSrc, Color.LightYellow * 0.12f, 0f, new Vector2(21.5f, 22f), 9f, SpriteEffects.None, 1.9E-05f);
            b.Draw(Game1.mouseCursors, moonPos + new Vector2(43 * 2.5f, 44 * 2.5f), moonSrc, Color.LightYellow * 0.22f, 0f, new Vector2(21.5f, 22f), 6.5f, SpriteEffects.None, 1.95E-05f);
            b.Draw(Game1.mouseCursors, moonPos, moonSrc, Color.White, 0f, Vector2.Zero, 5f, SpriteEffects.None, 2E-05f);

            // 별똥별: 1~3.5초마다 하나씩, 오른쪽 위에서 왼쪽 아래로
            if (now >= nextMeteorAt)
            {
                nextMeteorAt = now + 1.0 + skyRandom.NextDouble() * 2.5;
                float speed = 700f + (float)skyRandom.NextDouble() * 500f;
                float angle = MathHelper.ToRadians(150f + (float)skyRandom.NextDouble() * 20f);
                meteors.Add(new Meteor
                {
                    Pos = new Vector2(w * (0.25f + (float)skyRandom.NextDouble() * 0.85f), h * (float)skyRandom.NextDouble() * 0.3f),
                    Vel = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * speed,
                    MaxLife = 0.7f + (float)skyRandom.NextDouble() * 0.6f,
                    Length = 120f + (float)skyRandom.NextDouble() * 140f
                });
            }
            for (int i = meteors.Count - 1; i >= 0; i--)
            {
                Meteor m = meteors[i];
                m.Life += dt;
                m.Pos += m.Vel * dt;
                if (m.Life >= m.MaxLife) { meteors.RemoveAt(i); continue; }
                float fade = MathF.Sin(MathF.PI * (m.Life / m.MaxLife));   // 나타났다 사라짐
                Vector2 dir = Vector2.Normalize(m.Vel);
                float rot = MathF.Atan2(-dir.Y, -dir.X);   // 꼬리 방향
                for (int k = 0; k < 6; k++)
                {
                    float seg = m.Length * (k + 1) / 6f;
                    b.Draw(Game1.staminaRect, m.Pos, null, Color.White * (fade * (1f - k / 6f) * 0.55f), rot, Vector2.Zero,
                        new Vector2(seg, Math.Max(1f, 3f - k * 0.4f)), SpriteEffects.None, 2.1E-05f + k * 1E-08f);
                }
                b.Draw(Game1.staminaRect, m.Pos - new Vector2(3, 3), null, Color.LightCyan * fade, 0f, Vector2.Zero, 6f, SpriteEffects.None, 2.2E-05f);
            }
        }

        // 예전에 정상 맵(Maps\Summit)으로 만들어진 장소면 새 맵으로 바꾼다 (게임 서버에서, 바뀐 값은 플레이어에게 동기화됨).
        private void EnsureGoalMap()
        {
            GameLocation? loc = Game1.getLocationFromName(GoalLocationName);
            string path = GoalMapAsset.Replace('/', '\\');
            if (loc == null || loc.mapPath.Value == path) return;
            loc.mapPath.Value = path;
            loc.reloadMap();
            loc.loadMap(path, force_reload: true);
            Monitor.Log("약속의 정상 맵을 새 맵으로 바꿨습니다.", LogLevel.Info);
        }

        // 약속의 정상에 있는 동안은 다치거나 기절하지 않는다.
        private static void KeepSafeAtGoalPlace()
        {
            if (Game1.currentLocation?.NameOrUniqueName != GoalLocationName) return;
            if (Game1.player.health < Game1.player.maxHealth) Game1.player.health = Game1.player.maxHealth;
            if (Game1.player.Stamina < Game1.player.MaxStamina) Game1.player.Stamina = Game1.player.MaxStamina;
        }

        // ── 날짜 ──
        private static int SeasonIndexOf(string? season) => (season ?? "").Trim().ToLowerInvariant() switch
        {
            "spring" or "봄" => 0, "summer" or "여름" => 1, "fall" or "autumn" or "가을" => 2, _ => 3
        };
        private int DeadlineIndex => (Math.Max(1, config.GoalDeadlineYear) - 1) * 112 + SeasonIndexOf(config.GoalDeadlineSeason) * 28 + Math.Clamp(config.GoalDeadlineDay, 1, 28);
        private static int TodayIndex => (Game1.year - 1) * 112 + Game1.seasonIndex * 28 + Game1.dayOfMonth;
        private string DeadlineText => $"{Math.Max(1, config.GoalDeadlineYear)}년차 {new[] { "봄", "여름", "가을", "겨울" }[SeasonIndexOf(config.GoalDeadlineSeason)]} {Math.Clamp(config.GoalDeadlineDay, 1, 28)}일";

        private void CheckGoalDeadline(bool dayEnding)
        {
            if (!config.GoalEnabled || State.GoalAchieved || State.GoalFailed) return;
            bool over = dayEnding ? TodayIndex >= DeadlineIndex : TodayIndex > DeadlineIndex;
            if (!over) return;
            State.GoalFailed = true;
            goalProposal = null;
            SaveState();
            Notice(null, $"기한({DeadlineText})이 끝나 공동 목표에 실패했습니다. (모은 금액 {State.GoalSubmitted:N0} / {config.GoalGold:N0}골드)");
            Monitor.Log("공동 목표 실패: 기한 종료", LogLevel.Info);
            BroadcastGoal();
        }

        // ── 호스트 ──
        // 게임 서버의 호스트는 봇이라 투표에서 뺀다 (축제 투표와 같음).
        private static List<Farmer> GoalPlayers() => Game1.getOnlineFarmers().Where(f => f.UniqueMultiplayerID != Game1.player.UniqueMultiplayerID).ToList();

        private GoalStatusMessage BuildGoalStatus()
        {
            var s = new GoalStatusMessage
            {
                Enabled = config.GoalEnabled, Target = Math.Max(1, config.GoalGold), Submitted = State.GoalSubmitted,
                Achieved = State.GoalAchieved, Failed = State.GoalFailed, Deadline = DeadlineText
            };
            if (goalProposal is GoalProposal p)
            {
                var online = Game1.getOnlineFarmers().Select(f => f.UniqueMultiplayerID).ToHashSet();
                s.PendingId = p.Id; s.PendingProposer = p.FromName; s.PendingAmount = p.Amount;
                s.Voters = p.Voters.Where(online.Contains).ToList();
                s.Agreed = p.Agreed.Where(online.Contains).ToList();
            }
            return s;
        }

        private void BroadcastGoal()
        {
            if (!Context.IsMainPlayer || !Context.IsWorldReady) return;
            GoalStatusMessage s = BuildGoalStatus();
            foreach (Farmer f in Game1.getOnlineFarmers()) SendToPlayer(f.UniqueMultiplayerID, MsgGoalStatus, s);
        }

        private void OnHostGoalPropose(long fromId, long amount)
        {
            Farmer? who = FindOnlineFarmer(fromId);
            if (who == null) return;
            long target = Math.Max(1, config.GoalGold), remaining = target - State.GoalSubmitted;
            if (!config.GoalEnabled) { Notice(fromId, "공동 목표가 꺼져 있습니다."); return; }
            if (State.GoalAchieved) { Notice(fromId, "이미 목표를 달성했습니다."); return; }
            if (State.GoalFailed) { Notice(fromId, "기한이 지나 더 이상 제출할 수 없습니다."); return; }
            if (goalProposal != null) { Notice(fromId, $"{goalProposal.FromName}님의 제출 요청이 진행 중입니다. 끝난 뒤 다시 시도하세요."); return; }
            if (amount <= 0) { Notice(fromId, "제출할 금액을 입력하세요."); return; }
            amount = Math.Min(amount, remaining);   // 목표보다 많이 내지 않게
            if (amount > Game1.player.Money) { Notice(fromId, $"공용 돈이 부족합니다. (현재 {Game1.player.Money:N0}골드)"); return; }

            var p = new GoalProposal { From = fromId, FromName = who.Name, Amount = amount };
            foreach (Farmer f in GoalPlayers()) if (f.UniqueMultiplayerID != fromId) p.Voters.Add(f.UniqueMultiplayerID);
            if (p.Voters.Count == 0) { ExecuteGoalSubmit(p); return; }

            goalProposal = p;
            Notice(null, $"{who.Name}님이 공용 돈 {amount:N0}골드를 공동 목표에 제출하려고 합니다. 모두 동의하면 제출됩니다. (채팅 /동의 또는 /거절)");
            var req = new GoalVoteRequestMessage { Id = p.Id, Proposer = who.Name, Amount = amount };
            foreach (long id in p.Voters) SendToPlayer(id, MsgGoalVoteRequest, req);
            BroadcastGoal();
        }

        private void OnHostGoalVote(long fromId, GoalVoteMessage msg)
        {
            GoalProposal? p = goalProposal;
            if (p == null || p.Id != msg.Id) { Notice(fromId, "답할 제출 요청이 없습니다."); return; }
            if (!p.Voters.Contains(fromId)) { Notice(fromId, fromId == p.From ? "직접 요청한 제출은 다른 플레이어의 동의를 기다립니다." : "이 요청에는 투표할 수 없습니다."); return; }
            string name = FindOnlineFarmer(fromId)?.Name ?? "?";
            if (!msg.Agree)
            {
                goalProposal = null;
                Notice(null, $"{name}님이 거절해 {p.FromName}님의 {p.Amount:N0}골드 제출이 취소되었습니다.");
                BroadcastGoal();
                return;
            }
            if (!p.Agreed.Add(fromId)) return;
            TryFinishGoalVote(p, $"{name}님이 동의했습니다.");
        }

        private void TryFinishGoalVote(GoalProposal p, string? progressText)
        {
            var online = Game1.getOnlineFarmers().Select(f => f.UniqueMultiplayerID).ToHashSet();
            if (!online.Contains(p.From)) { goalProposal = null; Notice(null, $"{p.FromName}님이 나가서 제출 요청이 취소되었습니다."); BroadcastGoal(); return; }
            int need = p.Voters.Count(online.Contains), have = p.Agreed.Count(online.Contains);
            if (have >= need) { goalProposal = null; ExecuteGoalSubmit(p); return; }
            if (progressText != null) Notice(null, $"{progressText} (동의 {have}/{need})");
            BroadcastGoal();
        }

        private void ExecuteGoalSubmit(GoalProposal p)
        {
            long target = Math.Max(1, config.GoalGold);
            long amount = Math.Min(p.Amount, target - State.GoalSubmitted);
            if (amount <= 0 || State.GoalAchieved || State.GoalFailed) { BroadcastGoal(); return; }
            if (amount > Game1.player.Money) { Notice(null, $"공용 돈이 부족해 제출이 취소되었습니다. (현재 {Game1.player.Money:N0}골드)"); BroadcastGoal(); return; }

            Game1.player.Money -= (int)amount;   // 공용 지갑에서 빠진다
            State.GoalSubmitted += amount;
            Monitor.Log($"공동 목표 제출: {p.FromName} {amount}골드 → {State.GoalSubmitted}/{target}", LogLevel.Info);
            Notice(null, $"{p.FromName}님이 {amount:N0}골드를 제출했습니다! (모은 금액 {State.GoalSubmitted:N0} / {target:N0}골드)");
            CheckGoalAchieved();
            SaveState();
            BroadcastGoal();
        }

        // config.json의 GoalResetCount를 바꾸면 한 번 초기화한다.
        private void ResetGoal()
        {
            State.GoalResetApplied = config.GoalResetCount;
            long before = State.GoalSubmitted;
            State.GoalSubmitted = 0;
            State.GoalAchieved = false;
            State.GoalFailed = false;
            goalProposal = null;
            goalTrip = null;
            lastGoalTarget = -1;
            SaveState();
            Notice(null, $"공동 목표가 초기화되었습니다. 처음부터 다시 모아 주세요! (목표 {Math.Max(1, config.GoalGold):N0}골드, 기한 {DeadlineText})");
            Monitor.Log($"공동 목표 초기화 (이전 모은 금액 {before}골드)", LogLevel.Info);
            CheckGoalDeadline(dayEnding: false);   // 이미 기한이 지났으면 바로 실패로 표시
            BroadcastGoal();
        }
        private void CheckGoalAchieved()
        {
            if (State.GoalAchieved || State.GoalFailed || State.GoalSubmitted < Math.Max(1, config.GoalGold)) return;
            State.GoalAchieved = true;
            SaveState();
            Notice(null, "공동 목표 달성! 모두 수고하셨습니다. 화면 오른쪽 위 목표 아이콘(또는 /목표)에서 '약속의 정상'으로 이동할 수 있습니다.");
            Monitor.Log("공동 목표 달성", LogLevel.Info);
        }

        private void OnGoalUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady) return;
            KeepSafeAtGoalPlace();
            CheckLeaveGoalPlace();
            if (Context.IsMainPlayer)
            {
                if (!e.IsMultipleOf(30)) return;
                if (goalProposal is GoalProposal p)
                {
                    if (DateTime.UtcNow - p.At > GoalVoteTimeout)
                    {
                        goalProposal = null;
                        Notice(null, $"동의가 모이지 않아 {p.FromName}님의 {p.Amount:N0}골드 제출이 취소되었습니다.");
                        BroadcastGoal();
                    }
                    else TryFinishGoalVote(p, null);   // 투표할 사람이 나가면 다시 계산
                }
                if (goalTrip != null) TryFinishTrip();   // 시간 초과, 나간 사람 반영
                if (config.GoalResetCount != State.GoalResetApplied) ResetGoal();
                // config.json에서 목표 금액을 바꾸면 바로 반영
                if (config.GoalGold != lastGoalTarget) { lastGoalTarget = config.GoalGold; CheckGoalAchieved(); BroadcastGoal(); }
            }
            else if (ClientGoal == null && (DateTime.UtcNow - clientGoalAskedAt).TotalSeconds > 5)
            {
                clientGoalAskedAt = DateTime.UtcNow;
                SendToHost(MsgGoalRequest, new GoalRequestMessage());
            }
        }

        private void OnGoalModMessage(object? sender, ModMessageReceivedEventArgs e)
        {
            if (e.FromModID != ModManifest.UniqueID) return;
            try
            {
                if (Context.IsMainPlayer)
                {
                    if (FindOnlineFarmer(e.FromPlayerID) == null) return;
                    switch (e.Type)
                    {
                        case MsgGoalRequest: SendToPlayer(e.FromPlayerID, MsgGoalStatus, BuildGoalStatus()); break;
                        case MsgGoalPropose: OnHostGoalPropose(e.FromPlayerID, e.ReadAs<GoalProposeMessage>().Amount); break;
                        case MsgGoalVote:    OnHostGoalVote(e.FromPlayerID, e.ReadAs<GoalVoteMessage>()); break;
                        case MsgGoalTripRequest: OnHostTripRequest(e.FromPlayerID); break;
                        case MsgGoalTripVote:    OnHostTripVote(e.FromPlayerID, e.ReadAs<GoalTripVoteMessage>()); break;
                    }
                }
                else
                {
                    if (e.FromPlayerID != Game1.MasterPlayer.UniqueMultiplayerID) return;
                    switch (e.Type)
                    {
                        case MsgGoalStatus:      OnClientGoalStatus(e.ReadAs<GoalStatusMessage>()); break;
                        case MsgGoalVoteRequest: OnClientGoalVoteRequest(e.ReadAs<GoalVoteRequestMessage>()); break;
                        case MsgGoalTripAsk:     OnClientTripAsk(e.ReadAs<GoalTripAskMessage>()); break;
                        case MsgGoalTripGo:      DoGoalWarp(); break;
                    }
                }
            }
            catch (Exception ex) { Monitor.Log($"목표 메시지 처리 오류 ({e.Type}): {ex.Message}", LogLevel.Warn); }
        }

        // 호스트가 직접 플레이하는 경우: 자기 자신에게 보내는 메시지는 SendToPlayer가 바로 처리하지 못하므로 여기서 받는다.
        private void SendGoalToHost<T>(string type, T message) where T : notnull
        {
            if (Context.IsMainPlayer)
            {
                long me = Game1.player.UniqueMultiplayerID;
                switch (message)
                {
                    case GoalProposeMessage m: OnHostGoalPropose(me, m.Amount); break;
                    case GoalVoteMessage m:    OnHostGoalVote(me, m); break;
                    case GoalRequestMessage:   OnClientGoalStatus(BuildGoalStatus()); break;
                    case GoalTripRequestMessage: OnHostTripRequest(me); break;
                    case GoalTripVoteMessage m:  OnHostTripVote(me, m); break;
                }
                return;
            }
            SendToHost(type, message);
        }

        // ── 플레이어 게임 ──
        private void OnClientGoalStatus(GoalStatusMessage s)
        {
            ClientGoal = s;
            if (ClientGoalVote != null && ClientGoalVote.Id != s.PendingId) ClientGoalVote = null;   // 끝난 요청은 지운다
        }

        private void OnClientGoalVoteRequest(GoalVoteRequestMessage req)
        {
            ClientGoalVote = req;
            string text = $"{req.Proposer}님이 공용 돈 {req.Amount:N0}골드를 공동 목표에 제출하려고 합니다. 동의하시겠습니까?";
            if (Game1.activeClickableMenu == null && Context.IsPlayerFree)
                Game1.activeClickableMenu = new ConfirmationDialog(text, _ => AnswerGoalVote(true), _ => AnswerGoalVote(false));
            else
                Game1.addHUDMessage(new HUDMessage("공동 목표 제출 동의 요청이 왔습니다. 채팅 /동의 또는 /거절, 또는 목표 창에서 답하세요.", HUDMessage.newQuest_type));
        }

        public void AnswerGoalVote(bool agree)
        {
            GoalVoteRequestMessage? req = ClientGoalVote;
            if (req == null) { ChatInfo("답할 제출 요청이 없습니다."); return; }
            ClientGoalVote = null;
            if (Game1.activeClickableMenu is ConfirmationDialog) Game1.exitActiveMenu();
            SendGoalToHost(MsgGoalVote, new GoalVoteMessage { Id = req.Id, Agree = agree });
        }

        public void ProposeGoal(long amount) => SendGoalToHost(MsgGoalPropose, new GoalProposeMessage { Amount = amount });
        public void RequestGoalStatus() => SendGoalToHost(MsgGoalRequest, new GoalRequestMessage());

        private void OpenGoalMenu()
        {
            if (!Context.IsWorldReady) return;
            RequestGoalStatus();
            Game1.activeClickableMenu = new GoalMenu(this);
        }

        // [약속의 정상으로 이동]: 먼저 나에게 묻고, '예'면 서버가 다른 모두에게 묻는다.
        public void WarpToGoalPlace()
        {
            if (ClientGoal?.Achieved != true) return;
            Game1.activeClickableMenu = new ConfirmationDialog(GoalTripQuestion,
                _ => { Game1.exitActiveMenu(); SendGoalToHost(MsgGoalTripRequest, new GoalTripRequestMessage()); ChatInfo("다른 플레이어의 대답을 기다립니다..."); },
                _ => Game1.exitActiveMenu());
        }

        private GoalTripAskMessage? clientTripAsk;
        private void OnClientTripAsk(GoalTripAskMessage ask)
        {
            clientTripAsk = ask;
            void Answer(bool yes) { clientTripAsk = null; Game1.exitActiveMenu(); SendGoalToHost(MsgGoalTripVote, new GoalTripVoteMessage { Id = ask.Id, Agree = yes }); }
            string text = $"{ask.Requester}님이 모두 함께 약속의 정상으로 가자고 합니다.\n" + GoalTripQuestion;
            if (Game1.activeClickableMenu != null && Game1.activeClickableMenu is not GoalMenu) Game1.exitActiveMenu();
            Game1.activeClickableMenu = new ConfirmationDialog(text, _ => Answer(true), _ => Answer(false));
        }

        // 정상에 가기 전 위치. 정상 아래쪽 길로 내려가면 여기로 돌아온다 (이 PC의 모드 폴더에 저장).
        private sealed class GoalReturnPoint { public string Location { get; set; } = ""; public bool IsStructure { get; set; } public int X { get; set; } public int Y { get; set; } public int Facing { get; set; } = 2; }
        private string GoalReturnFile => $"goal-return/{Game1.player.UniqueMultiplayerID}.json";
        private bool leavingGoalPlace;

        private void DoGoalWarp()
        {
            Game1.exitActiveMenu();
            GameLocation? here = Game1.currentLocation;
            if (here == null || here.NameOrUniqueName == GoalLocationName) return;
            Point tile = Game1.player.TilePoint;
            Helper.Data.WriteJsonFile(GoalReturnFile, new GoalReturnPoint
            {
                Location = here.NameOrUniqueName, IsStructure = here.isStructure.Value, X = tile.X, Y = tile.Y, Facing = Game1.player.FacingDirection
            });
            Game1.warpFarmer(GoalLocationName, 10, 20, 0);
            DelayedAction.functionAfterDelay(() => ChatInfo("아래쪽 길로 내려가면 원래 있던 곳으로 돌아갑니다."), 1500);
        }

        // 정상 맨 아래 길(출구)에 닿으면 원래 위치로 보낸다. 기록이 없으면 농장 집 앞으로.
        private void CheckLeaveGoalPlace()
        {
            if (Game1.currentLocation?.NameOrUniqueName != GoalLocationName) { leavingGoalPlace = false; return; }
            if (leavingGoalPlace || Game1.isWarping || !Context.IsPlayerFree || Game1.player.TilePoint.Y < 29) return;
            leavingGoalPlace = true;
            GoalReturnPoint? back = null;
            try { back = Helper.Data.ReadJsonFile<GoalReturnPoint>(GoalReturnFile); } catch { }
            GameLocation? target = back == null || back.Location == "" ? null : Game1.getLocationFromName(back.Location, back.IsStructure);
            if (back != null && target != null)
            {
                Game1.warpFarmer(Game1.getLocationRequest(back.Location, back.IsStructure), back.X, back.Y, back.Facing);
            }
            else
            {
                Game1.warpFarmer("Farm", 64, 15, 2);
            }
            Helper.Data.WriteJsonFile<GoalReturnPoint>(GoalReturnFile, null);
        }

        // 호스트: 이동 투표
        private void OnHostTripRequest(long fromId)
        {
            Farmer? who = FindOnlineFarmer(fromId);
            if (who == null) return;
            if (!State.GoalAchieved) { Notice(fromId, "아직 공동 목표를 달성하지 않았습니다."); return; }
            if (goalTrip != null) { Notice(fromId, "이미 이동 여부를 묻는 중입니다."); return; }
            var t = new GoalProposal { From = fromId, FromName = who.Name };
            t.Agreed.Add(fromId);
            foreach (Farmer f in GoalPlayers()) if (f.UniqueMultiplayerID != fromId) t.Voters.Add(f.UniqueMultiplayerID);
            goalTrip = t;
            if (t.Voters.Count > 0)
            {
                Notice(null, $"{who.Name}님이 약속의 정상으로 이동하자고 합니다. 모두 '예'를 누르면 함께 이동합니다.");
                var ask = new GoalTripAskMessage { Id = t.Id, Requester = who.Name };
                foreach (long id in t.Voters) SendToPlayer(id, MsgGoalTripAsk, ask);
            }
            TryFinishTrip();
        }

        private void OnHostTripVote(long fromId, GoalTripVoteMessage msg)
        {
            GoalProposal? t = goalTrip;
            if (t == null || t.Id != msg.Id || !t.Voters.Contains(fromId)) return;
            string name = FindOnlineFarmer(fromId)?.Name ?? "?";
            if (!msg.Agree)
            {
                goalTrip = null;
                Notice(null, $"{name}님이 '아니오'를 눌러 약속의 정상으로 이동하지 않습니다.");
                return;
            }
            t.Agreed.Add(fromId);
            TryFinishTrip();
        }

        private void TryFinishTrip()
        {
            GoalProposal? t = goalTrip;
            if (t == null) return;
            var online = Game1.getOnlineFarmers().Select(f => f.UniqueMultiplayerID).ToHashSet();
            if (!online.Contains(t.From)) { goalTrip = null; Notice(null, $"{t.FromName}님이 나가서 이동을 취소했습니다."); return; }
            if (DateTime.UtcNow - t.At > TimeSpan.FromSeconds(60)) { goalTrip = null; Notice(null, "대답이 모이지 않아 약속의 정상 이동을 취소했습니다."); return; }
            if (t.Voters.Where(online.Contains).Any(id => !t.Agreed.Contains(id))) return;
            goalTrip = null;
            Notice(null, "모두 동의했습니다! 함께 약속의 정상으로 이동합니다.");
            foreach (Farmer f in Game1.getOnlineFarmers()) SendToPlayer(f.UniqueMultiplayerID, MsgGoalTripGo, new GoalTripGoMessage());
            Monitor.Log("약속의 정상으로 모두 이동", LogLevel.Info);
        }

        private void UpdateGoalIconRect()
        {
            DayTimeMoneyBox box = Game1.dayTimeMoneyBox;
            goalIconRect = new Rectangle(box.xPositionOnScreen + 212, box.yPositionOnScreen + 296, 64, 64);
        }

        private void OnGoalRenderedHud(object? sender, RenderedHudEventArgs e)
        {
            if (!Context.IsWorldReady || ClientGoal?.Enabled != true || Game1.eventUp || Game1.dayTimeMoneyBox == null) return;
            UpdateGoalIconRect();
            SpriteBatch b = e.SpriteBatch;
            bool hover = goalIconRect.Contains(Game1.getMouseX(true), Game1.getMouseY(true));
            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                goalIconRect.X, goalIconRect.Y, goalIconRect.Width, goalIconRect.Height, hover ? Color.Wheat : Color.White, 1f, false);
            goalIconItem ??= ItemRegistry.Create("(O)336");   // 금괴
            goalIconItem.drawInMenu(b, new Vector2(goalIconRect.X, goalIconRect.Y), 0.8f, 1f, 0.9f, StackDrawType.Hide);
            // 남은 비율 표시 (아이콘 아래 얇은 막대)
            float ratio = Math.Clamp(ClientGoal.Submitted / (float)Math.Max(1, ClientGoal.Target), 0f, 1f);
            b.Draw(Game1.staminaRect, new Rectangle(goalIconRect.X + 8, goalIconRect.Bottom - 12, goalIconRect.Width - 16, 6), Color.Black * 0.5f);
            b.Draw(Game1.staminaRect, new Rectangle(goalIconRect.X + 8, goalIconRect.Bottom - 12, (int)((goalIconRect.Width - 16) * ratio), 6),
                ClientGoal.Failed ? Color.Gray : ClientGoal.Achieved ? Color.Gold : Color.LimeGreen);
            if (!string.IsNullOrEmpty(ClientGoal.PendingId) && Game1.ticks / 30 % 2 == 0)
                b.Draw(Game1.mouseCursors, new Vector2(goalIconRect.Right - 18, goalIconRect.Y - 6), new Rectangle(403, 496, 5, 14), Color.White, 0f, Vector2.Zero, 3f, SpriteEffects.None, 1f);   // 동의 대기 중 느낌표
            if (hover && Game1.activeClickableMenu == null)
            {
                string tip = ClientGoal.Achieved ? "공동 목표 달성!" : ClientGoal.Failed ? "공동 목표 실패"
                    : $"공동 목표: {ClientGoal.Submitted:N0} / {ClientGoal.Target:N0}골드";
                IClickableMenu.drawHoverText(b, tip, Game1.smallFont);
            }
        }

        private void OnGoalButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            if (e.Button != SButton.MouseLeft || !Context.IsWorldReady || ClientGoal?.Enabled != true) return;
            if (Game1.activeClickableMenu != null || Game1.eventUp || Game1.dayTimeMoneyBox == null) return;
            UpdateGoalIconRect();
            if (!goalIconRect.Contains(Game1.getMouseX(true), Game1.getMouseY(true))) return;
            Helper.Input.Suppress(e.Button);
            Game1.playSound("smallSelect");
            OpenGoalMenu();
        }
    }

    // ── 목표 창 ─────────────────────────────────────────────────────────────
    internal sealed class GoalMenu : IClickableMenu
    {
        private const int W = 760, H = 580;
        private readonly ModEntry _mod;
        private readonly TextBox _amount;
        private Rectangle _maxBtn, _submitBtn, _agreeBtn, _rejectBtn, _warpBtn;
        private ClickableTextureComponent _closeBtn = null!;

        public GoalMenu(ModEntry mod) : base((Game1.uiViewport.Width - W) / 2, (Game1.uiViewport.Height - H) / 2, W, H)
        {
            _mod = mod;
            _amount = new TextBox(Game1.content.Load<Texture2D>("LooseSprites\\textBox"), null, Game1.smallFont, Game1.textColor)
            { numbersOnly = true, textLimit = 10, Width = 300 };
            _amount.OnEnterPressed += _ => Submit();
            Layout();
            Game1.playSound("bigSelect");
        }

        private void Layout()
        {
            int x = xPositionOnScreen, y = yPositionOnScreen;
            // 입력칸 | 최대 | 제출 요청  (가운데 정렬)
            int rowW = 300 + 12 + 120 + 12 + 190, rowX = x + (W - rowW) / 2;
            _amount.X = rowX; _amount.Y = y + 286;
            _maxBtn    = new Rectangle(rowX + 312, y + 278, 120, 60);
            _submitBtn = new Rectangle(rowX + 444, y + 278, 190, 60);
            _agreeBtn  = new Rectangle(x + W / 2 - 220, y + 488, 200, 60);
            _rejectBtn = new Rectangle(x + W / 2 + 20,  y + 488, 200, 60);
            _warpBtn   = new Rectangle(x + (W - 420) / 2, y + 340, 420, 64);
            _closeBtn  = new ClickableTextureComponent(new Rectangle(x + W - 44, y - 4, 48, 48), Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 4f);
        }

        private GoalStatusMessage? S => _mod.ClientGoal;
        private bool MyVotePending => _mod.ClientGoalVote != null && S?.PendingId == _mod.ClientGoalVote.Id;
        private bool CanSubmit => S is { Enabled: true, Achieved: false, Failed: false } && string.IsNullOrEmpty(S.PendingId);
        private long Remaining => S == null ? 0 : Math.Max(0, S.Target - S.Submitted);

        private void Submit()
        {
            if (!CanSubmit) return;
            if (!long.TryParse(_amount.Text, out long v) || v <= 0) { Game1.playSound("cancel"); return; }
            _mod.ProposeGoal(v);
            _amount.Text = "";
            Game1.playSound("purchase");
        }

        // 창 안쪽 폭에 맞춰 줄을 바꿔 가운데 정렬로 그린다. 한 단어가 너무 길면 글자를 줄인다. 반환: 그린 높이
        private int DrawText(SpriteBatch b, string text, int y, Color color, SpriteFont? font = null)
        {
            font ??= Game1.smallFont;
            int maxW = width - 96;
            string wrapped = string.Join("\n", text.Split('\n').Select(p => Game1.parseText(p, font, maxW)));
            int h = 0;
            foreach (string line in wrapped.Split('\n'))
            {
                Vector2 size = font.MeasureString(line);
                float scale = size.X > maxW ? maxW / size.X : 1f;
                b.DrawString(font, line, new Vector2(xPositionOnScreen + (width - size.X * scale) / 2f, y + h), color, 0f, Vector2.Zero, scale, SpriteEffects.None, 1f);
                h += (int)(size.Y * scale);
            }
            return h;
        }

        private static void DrawButton(SpriteBatch b, Rectangle r, string label, bool enabled)
        {
            bool hover = enabled && r.Contains(Game1.getMouseX(), Game1.getMouseY());
            drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9), r.X, r.Y, r.Width, r.Height,
                !enabled ? Color.Gray * 0.6f : hover ? Color.Wheat : Color.White, 4f, false);
            Vector2 s = Game1.dialogueFont.MeasureString(label);
            float scale = Math.Min(1f, (r.Width - 16) / s.X);
            b.DrawString(Game1.dialogueFont, label, new Vector2(r.X + (r.Width - s.X * scale) / 2f, r.Y + (r.Height - s.Y * scale) / 2f + 4),
                enabled ? Game1.textColor : Game1.textColor * 0.4f, 0f, Vector2.Zero, scale, SpriteEffects.None, 1f);
        }

        public override void draw(SpriteBatch b)
        {
            b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.4f);
            drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), xPositionOnScreen, yPositionOnScreen, width, height, Color.White, 1f, false);
            DrawText(b, "공동 목표", yPositionOnScreen + 28, Game1.textColor, Game1.dialogueFont);

            GoalStatusMessage? s = S;
            if (s == null) { DrawText(b, "서버에서 정보를 받는 중...", yPositionOnScreen + 200, Game1.textColor * 0.7f); }
            else
            {
                DrawText(b, $"모은 금액  {s.Submitted:N0} / {s.Target:N0}골드", yPositionOnScreen + 96, Game1.textColor, Game1.dialogueFont);
                // 진행 막대
                var bar = new Rectangle(xPositionOnScreen + 44, yPositionOnScreen + 152, width - 88, 22);
                b.Draw(Game1.staminaRect, bar, Color.Black * 0.35f);
                float ratio = Math.Clamp(s.Submitted / (float)Math.Max(1, s.Target), 0f, 1f);
                b.Draw(Game1.staminaRect, new Rectangle(bar.X, bar.Y, (int)(bar.Width * ratio), bar.Height), s.Achieved ? Color.Gold : Color.LimeGreen);
                DrawText(b, $"기한: {s.Deadline}이 끝날 때까지", yPositionOnScreen + 192, Game1.textColor * 0.8f);
                DrawText(b, $"공용 돈: {Game1.player.Money:N0}골드", yPositionOnScreen + 228, Game1.textColor * 0.8f);

                if (s.Achieved)
                {
                    DrawText(b, "목표 달성! 모두 수고하셨습니다.", yPositionOnScreen + 276, Color.DarkGoldenrod, Game1.dialogueFont);
                    DrawButton(b, _warpBtn, "약속의 정상으로 이동", true);
                }
                else if (s.Failed)
                {
                    DrawText(b, "기한이 지나 목표에 실패했습니다.", yPositionOnScreen + 296, Color.DarkRed, Game1.dialogueFont);
                }
                else
                {
                    _amount.Draw(b);
                    if (string.IsNullOrEmpty(_amount.Text) && !_amount.Selected)
                        b.DrawString(Game1.smallFont, "제출할 금액 입력", new Vector2(_amount.X + 16, _amount.Y + 12), Game1.textColor * 0.45f);
                    DrawButton(b, _maxBtn, "최대", CanSubmit);
                    DrawButton(b, _submitBtn, "제출 요청", CanSubmit);
                    DrawText(b, "제출하면 공용 돈에서 빠지고 돌려받을 수 없습니다.\n접속 중인 모두가 동의해야 제출됩니다.", yPositionOnScreen + 360, Game1.textColor * 0.6f);

                    if (!string.IsNullOrEmpty(s.PendingId))
                    {
                        DrawText(b, $"{s.PendingProposer}님의 {s.PendingAmount:N0}골드 제출 — 동의 {s.Agreed.Count}/{s.Voters.Count}", yPositionOnScreen + 444, Color.DarkBlue);
                        if (MyVotePending)
                        {
                            DrawButton(b, _agreeBtn, "동의", true);
                            DrawButton(b, _rejectBtn, "거절", true);
                        }
                    }
                }
            }
            _closeBtn.draw(b);
            drawMouse(b);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (_closeBtn.containsPoint(x, y)) { Game1.playSound("bigDeSelect"); exitThisMenu(); return; }
            GoalStatusMessage? s = S;
            if (s == null) return;
            if (s.Achieved) { if (_warpBtn.Contains(x, y)) _mod.WarpToGoalPlace(); return; }
            if (s.Failed) return;

            _amount.Update();   // 입력칸을 누르면 선택된다
            if (MyVotePending && _agreeBtn.Contains(x, y)) { _mod.AnswerGoalVote(true); Game1.playSound("coin"); return; }
            if (MyVotePending && _rejectBtn.Contains(x, y)) { _mod.AnswerGoalVote(false); Game1.playSound("cancel"); return; }
            if (!CanSubmit) return;
            if (_maxBtn.Contains(x, y)) { _amount.Text = Math.Max(0, Math.Min(Remaining, Game1.player.Money)).ToString(); Game1.playSound("smallSelect"); return; }
            if (_submitBtn.Contains(x, y)) Submit();
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape) { Game1.playSound("bigDeSelect"); exitThisMenu(); return; }
            if (_amount.Selected) return;   // 숫자를 입력하는 중에는 다른 키를 무시
            if (Game1.options.doesInputListContain(Game1.options.menuButton, key)) { Game1.playSound("bigDeSelect"); exitThisMenu(); }
        }

        protected override void cleanupBeforeExit()
        {
            _amount.Selected = false;
            if (Game1.keyboardDispatcher.Subscriber == _amount) Game1.keyboardDispatcher.Subscriber = null;
            base.cleanupBeforeExit();
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;
            Layout();
        }
    }
}
