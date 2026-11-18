
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.Objects;
using StardewValley.Menus;
using StardewValley.Monsters;

namespace SoopStardewBridge
{
    // ── 호스트 ↔ 플레이어 메시지 ───────────────────────────────────────────
    // 보상 대기열(state.json)과 아이템 목록(loot.json)은 호스트(Docker 서버)에만 있다.
    // 플레이어 게임은 창을 띄우고 자기 인벤토리를 바꾸는 일만 하며, 필요한 정보는 SMAPI 멀티플레이 메시지로 주고받는다.
    internal sealed class PendingRequestMessage { public string Reason { get; set; } = "query"; }
    internal sealed class PendingMessage        { public int Chests { get; set; } public int PremiumChests { get; set; } public string Reason { get; set; } = "query"; }
    internal sealed class ClaimRequestMessage   { public string Kind { get; set; } = "all"; }   // chest | premiumChest | all
    internal sealed class GrantMessage          { public int Chests { get; set; } public int PremiumChests { get; set; } public int RemainingChests { get; set; } public int RemainingPremiumChests { get; set; } }
    internal sealed class ReturnMessage         { public int Chests { get; set; } public int PremiumChests { get; set; } }
    internal sealed class OpenRequestMessage    { public string Kind { get; set; } = "chest"; }
    internal sealed class OpenResultMessage     { public string Kind { get; set; } = "chest"; public string ItemId { get; set; } = ""; public int Amount { get; set; } public int Gold { get; set; } public string Error { get; set; } = ""; }
    internal sealed class LinkRequestMessage    { }
    internal sealed class LinkResponseMessage   { public string LoginUrl { get; set; } = ""; public string PageUrl { get; set; } = ""; public string SessionKey { get; set; } = ""; public string Error { get; set; } = ""; }
    internal sealed class UnlinkRequestMessage  { }
    internal sealed class UnlinkResponseMessage { public bool Ok { get; set; } public string Error { get; set; } = ""; }
    internal sealed class SavedLink             { public string PageUrl { get; set; } = ""; public string SessionKey { get; set; } = ""; public string SavedAt { get; set; } = ""; }

    // ── 보상 수령 UI (플레이어 게임에서 표시) ──────────────────────────────
    internal sealed class RewardClaimMenu : IClickableMenu
    {
        private const int MenuWidth  = 600;
        private const int MenuHeight = 420;

        private readonly ModEntry _mod;
        private readonly Item _normalIcon;
        private readonly Item _premiumIcon;
        private Rectangle _normalBtn, _premiumBtn, _allBtn;
        private ClickableTextureComponent _closeBtn = null!;

        public RewardClaimMenu(ModEntry mod) : base(
            (Game1.uiViewport.Width  - MenuWidth)  / 2,
            (Game1.uiViewport.Height - MenuHeight) / 2,
            MenuWidth, MenuHeight)
        {
            _mod = mod;
            _normalIcon  = ItemRegistry.Create(ModEntry.NormalChestId, 1);
            _premiumIcon = ItemRegistry.Create(ModEntry.PremiumChestId, 1);
            RebuildButtons();
            Game1.playSound("shwip");
        }

        private int RowY(int index) => yPositionOnScreen + 104 + index * 84;

        private void RebuildButtons()
        {
            int btnX = xPositionOnScreen + width - 64 - 140;
            _normalBtn  = new Rectangle(btnX, RowY(0) + 2, 140, 60);
            _premiumBtn = new Rectangle(btnX, RowY(1) + 2, 140, 60);
            _allBtn     = new Rectangle(xPositionOnScreen + width / 2 - 130, yPositionOnScreen + height - 100, 260, 64);
            _closeBtn = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 44, yPositionOnScreen - 4, 48, 48),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 4f);
        }

        private void DrawCentered(SpriteBatch b, string text, float y, Color color)
        {
            Vector2 size = Game1.dialogueFont.MeasureString(text);
            b.DrawString(Game1.dialogueFont, text, new Vector2(xPositionOnScreen + (width - size.X) / 2f, y), color);
        }

        private void DrawButton(SpriteBatch b, Rectangle r, string label, bool enabled)
        {
            bool hover = enabled && r.Contains(Game1.getMouseX(), Game1.getMouseY());
            Color tint = !enabled ? Color.Gray * 0.6f : hover ? Color.Wheat : Color.White;
            drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9), r.X, r.Y, r.Width, r.Height, tint, 4f, false);
            Vector2 s = Game1.dialogueFont.MeasureString(label);
            b.DrawString(Game1.dialogueFont, label, new Vector2(r.X + (r.Width - s.X) / 2f, r.Y + (r.Height - s.Y) / 2f + 4),
                enabled ? Game1.textColor : Game1.textColor * 0.4f);
        }

        private void DrawRow(SpriteBatch b, int index, Item icon, string name, int count, Rectangle btn)
        {
            int y = RowY(index);
            icon.drawInMenu(b, new Vector2(xPositionOnScreen + 40, y), 1f);
            b.DrawString(Game1.dialogueFont, name, new Vector2(xPositionOnScreen + 120, y + 14), Game1.textColor);
            string countText = $"{count} 개";
            Vector2 cs = Game1.dialogueFont.MeasureString(countText);
            b.DrawString(Game1.dialogueFont, countText, new Vector2(btn.X - 28 - cs.X, y + 14), count > 0 ? Game1.textColor : Game1.textColor * 0.45f);
            DrawButton(b, btn, "수령", !_mod.ClientWaiting && count > 0);
        }

        public override void draw(SpriteBatch b)
        {
            b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.4f);
            drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                xPositionOnScreen, yPositionOnScreen, width, height, Color.White, 1f, false);

            DrawCentered(b, "SOOP 후원 보상", yPositionOnScreen + 28, Game1.textColor);
            b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen + 24, yPositionOnScreen + 84, width - 48, 2), Color.Gray * 0.5f);

            if (_mod.ClientWaiting && (DateTime.UtcNow - _mod.ClientWaitingSince).TotalSeconds > 6)
            {
                DrawCentered(b, "서버가 응답하지 않습니다.", RowY(0) + 20, Game1.textColor * 0.7f);
                DrawCentered(b, "서버에 모드가 설치되어 있는지 확인하세요.", RowY(1) + 10, Game1.textColor * 0.6f);
            }
            else
            {
                int normal  = _mod.ClientPendingChests;
                int premium = _mod.ClientPendingPremiumChests;
                DrawRow(b, 0, _normalIcon,  "일반 상자", normal,  _normalBtn);
                DrawRow(b, 1, _premiumIcon, "고급 상자", premium, _premiumBtn);
                DrawButton(b, _allBtn, "전체 수령", !_mod.ClientWaiting && normal + premium > 0);
            }

            _closeBtn.draw(b);
            drawMouse(b);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (_closeBtn.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                exitThisMenu();
                return;
            }
            if (_mod.ClientWaiting) return;

            if      (_normalBtn.Contains(x, y)  && _mod.ClientPendingChests > 0)        _mod.RequestClaim(ModEntry.KindNormal);
            else if (_premiumBtn.Contains(x, y) && _mod.ClientPendingPremiumChests > 0) _mod.RequestClaim(ModEntry.KindPremium);
            else if (_allBtn.Contains(x, y)     && _mod.ClientPendingChests + _mod.ClientPendingPremiumChests > 0) _mod.RequestClaim(ModEntry.KindAll);
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape || Game1.options.doesInputListContain(Game1.options.menuButton, key))
            {
                Game1.playSound("bigDeSelect");
                exitThisMenu();
            }
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            xPositionOnScreen = (Game1.uiViewport.Width  - width)  / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;
            RebuildButtons();
        }
    }

    // ── 메인 모드 ─────────────────────────────────────────────────────────────
    internal sealed class ModEntry : Mod
    {
        // 일반 상자는 예전 ID를 그대로 써서 이미 받은 상자도 일반 상자가 된다.
        public const string NormalChestId  = "SoopStardewBridge_RewardChest";
        public const string PremiumChestId = "SoopStardewBridge_PremiumChest";
        private const string NormalTextureAsset  = "SoopStardewBridge/reward_chest";
        private const string PremiumTextureAsset = "SoopStardewBridge/premium_chest";
        private const int    MaxGrantPerClaim = 999;   // 한 칸에 쌓을 수 있는 최대 개수

        public const string KindNormal  = "chest";
        public const string KindPremium = "premiumChest";
        public const string KindAll     = "all";

        private const string MsgPendingRequest = "PendingRequest";  // 플레이어 → 호스트
        private const string MsgPending        = "Pending";         // 호스트 → 플레이어
        private const string MsgClaimRequest   = "ClaimRequest";    // 플레이어 → 호스트
        private const string MsgGrant          = "Grant";           // 호스트 → 플레이어
        private const string MsgReturn         = "Return";          // 플레이어 → 호스트 (인벤토리에 못 넣은 개수)
        private const string MsgOpenRequest    = "OpenRequest";     // 플레이어 → 호스트 (상자 열기)
        private const string MsgOpenResult     = "OpenResult";      // 호스트 → 플레이어 (뽑힌 아이템)
        private const string MsgLinkRequest    = "LinkRequest";     // 플레이어 → 호스트 (/숲로그인)
        private const string MsgLinkResponse   = "LinkResponse";    // 호스트 → 플레이어 (로그인 주소, 세션 키)
        private const string MsgUnlinkRequest  = "UnlinkRequest";   // 플레이어 → 호스트 (/숲로그아웃)
        private const string MsgUnlinkResponse = "UnlinkResponse";  // 호스트 → 플레이어

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly ConcurrentQueue<DonationEvent> incomingEvents      = new();
        private readonly ConcurrentQueue<CentralJob>    incomingCentralJobs = new();
        private readonly HashSet<long>                  loggedFarmerIds     = new();
        private readonly Random                         random              = new();
        private readonly HttpClient                     centralHttp         = new() { Timeout = TimeSpan.FromSeconds(12) };

        // 호스트: 마지막으로 보낸 지급 수량. 플레이어가 돌려보내는 양은 이 값을 넘을 수 없다.
        private readonly Dictionary<long, (int Chests, int Premium)> lastGrant = new();

        // 플레이어 게임 쪽 표시용 값 (호스트가 알려준 값)
        public int      ClientPendingChests;
        public int      ClientPendingPremiumChests;
        public bool     ClientWaiting;
        public DateTime ClientWaitingSince;
        private bool     clientOpening;
        private DateTime clientOpeningSince;
        private bool     clientLinkPending;

        // 호스트: Central 응답을 게임 스레드에서 플레이어에게 보내기 위한 대기열
        private readonly ConcurrentQueue<Action> hostMainThreadActions = new();
        // 호스트: Central 폴링 때 함께 보내는 "지금 접속 중인 플레이어" 목록
        private volatile string[] onlineFarmerSnapshot = Array.Empty<string>();

        // 플레이어 게임: 숨은 Edge (SOOP 채팅 연결용)
        private System.Diagnostics.Process? edgeProcess;

        // config/rules/loot는 백그라운드 타이머가 교체하고 다른 스레드가 읽으므로 volatile로 둔다.
        private volatile BridgeConfig config = new();
        private volatile RulesFile    rules  = RulesFile.CreateDefault();
        private volatile LootFile     loot   = LootFile.CreateDefault();
        private volatile MonstersFile monsters = MonstersFile.CreateDefault();
        private volatile bool         lootNeedsCheck = true;
        public  BridgeState  State  = new();

        private const string ConfigFileName = "config.json";
        private const string RulesFileName  = "rules.json";
        private const string LootFileName   = "loot.json";
        private const string MonstersFileName = "monsters.json";
        private DateTime monstersWriteUtc;
        private DateTime configWriteUtc;
        private DateTime rulesWriteUtc;
        private DateTime lootWriteUtc;
        private System.Threading.Timer? hotReloadTimer;

        private HttpListener?            listener;
        private CancellationTokenSource? listenerCancellation;
        private CancellationTokenSource? centralCancellation;

        // ── 초기화 ───────────────────────────────────────────────────────────

        public override void Entry(IModHelper helper)
        {
            config = Helper.ReadConfig<BridgeConfig>();

            if (string.IsNullOrWhiteSpace(config.AuthToken))
            {
                config.AuthToken = Guid.NewGuid().ToString("N");
                Helper.WriteConfig(config);
            }

            State = Helper.Data.ReadJsonFile<BridgeState>("state.json") ?? new BridgeState();

            configWriteUtc = StampOf(ConfigFileName);
            EnsureRulesFile();
            ReloadRules(force: true);
            EnsureLootFile();
            ReloadLoot(force: true);
            EnsureMonstersFile();
            ReloadMonsters(force: true);

            Helper.Events.GameLoop.SaveLoaded             += OnSaveLoaded;
            Helper.Events.GameLoop.DayStarted             += OnDayStarted;
            Helper.Events.GameLoop.UpdateTicked           += OnUpdateTicked;
            Helper.Events.Input.ButtonPressed             += OnButtonPressed;
            Helper.Events.Content.AssetRequested          += OnAssetRequested;
            Helper.Events.Multiplayer.ModMessageReceived  += OnModMessageReceived;
            Helper.Events.GameLoop.ReturnedToTitle        += (_, _) => StopEdge("타이틀 화면으로 나감");
            AppDomain.CurrentDomain.ProcessExit           += (_, _) => StopEdge("게임 종료");
            CleanupOrphanEdge();   // 지난번에 게임이 튕겨서 남은 숨은 Edge가 있으면 끈다

            RegisterChatCommand();
            StartHttpListener();
            StartCentralPolling();

            // 2초마다 config.json / rules.json / loot.json 수정 여부를 확인한다. 게임 루프와 무관하게 동작한다.
            hotReloadTimer = new System.Threading.Timer(_ => HotReload(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

            Monitor.Log("SOOP Stardew Bridge가 시작되었습니다.", LogLevel.Info);
        }

        // 채팅창에 /우편함 을 입력하면 보상 창이 열린다. (/mailbox 도 같음)
        private void RegisterChatCommand()
        {
            try
            {
                ChatCommands.Register(
                    ModManifest.UniqueID + "_mailbox",
                    (command, chat) => OpenClaimMenu(),
                    name => "SOOP 후원 보상 수령 창을 엽니다.",
                    aliases: new[] { "우편함", "mailbox" });
                ChatCommands.Register(
                    ModManifest.UniqueID + "_login",
                    (command, chat) => StartSoopLogin(),
                    name => "SOOP 계정을 연결합니다. 게임이 켜져 있는 동안 내 방송의 별풍선 후원을 받습니다.",
                    aliases: new[] { "숲로그인", "soop-login" });
                ChatCommands.Register(
                    ModManifest.UniqueID + "_logout",
                    (command, chat) => StartSoopLogout(),
                    name => "SOOP 계정 연결을 해제하고 연결 정보를 삭제합니다.",
                    aliases: new[] { "숲로그아웃", "soop-logout" });
                Monitor.Log("채팅 명령어 등록: /우편함, /mailbox, /숲로그인, /숲로그아웃", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Monitor.Log($"채팅 명령어 등록 실패 (F5는 사용 가능): {ex.Message}", LogLevel.Warn);
            }
        }

        // ── 이벤트 핸들러 ────────────────────────────────────────────────────

        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            if (!Context.IsMainPlayer)
            {
                // 플레이어 게임: 접속하면 호스트에 내 대기 보상을 물어본다 (있으면 HUD 알림).
                ClientPendingChests = 0;
                ClientPendingPremiumChests = 0;
                ClientWaiting = false;
                clientOpening = false;
                SendToHost(MsgPendingRequest, new PendingRequestMessage { Reason = "login" });
                ResumeSavedLink();   // 전에 /숲로그인 했으면 숨은 Edge를 다시 띄운다
                return;
            }

            State = Helper.Data.ReadJsonFile<BridgeState>("state.json") ?? State;
            loggedFarmerIds.Clear();
            SaveState();
            LogOnlineFarmers();
            ProcessPendingRewards();

            Monitor.Log(
                $"처리 이벤트: {State.ProcessedEventIds.Count}, " +
                $"대기 일반 상자 대상: {State.PendingChests.Count}, " +
                $"대기 고급 상자 대상: {State.PendingPremiumChests.Count}, " +
                $"대기 몬스터 대상: {State.PendingMonsters.Count}",
                LogLevel.Info);
        }

        private void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            if (Context.IsMainPlayer) ProcessPendingRewards();
        }

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            // 아이템 목록 확인은 게임 데이터가 준비된 뒤 게임 스레드에서 한다.
            if (lootNeedsCheck && Context.IsGameLaunched && Context.IsMainPlayer)
            {
                lootNeedsCheck = false;
                CheckLootItems(loot);
            }

            // 상자를 열었는데 서버가 답하지 않으면 다시 열 수 있게 풀어 준다.
            if (clientOpening && (DateTime.UtcNow - clientOpeningSince).TotalSeconds > 8)
            {
                clientOpening = false;
                Game1.addHUDMessage(new HUDMessage("서버가 응답하지 않아 상자를 열지 못했습니다.", HUDMessage.error_type));
            }

            if (!Context.IsWorldReady || !Context.IsMainPlayer) return;

            while (hostMainThreadActions.TryDequeue(out Action? action))
            {
                try { action(); } catch (Exception ex) { Monitor.Log($"호스트 처리 오류: {ex.Message}", LogLevel.Warn); }
            }

            if (e.IsMultipleOf(60))
            {
                LogOnlineFarmers();
                onlineFarmerSnapshot = Game1.getOnlineFarmers().Select(f => f.UniqueMultiplayerID.ToString()).ToArray();
            }

            while (incomingEvents.TryDequeue(out DonationEvent? d))
                if (d != null) ProcessDonation(d);

            while (incomingCentralJobs.TryDequeue(out CentralJob? j))
            {
                if (j != null)
                {
                    bool ok = ProcessCentralJob(j);
                    if (!string.IsNullOrWhiteSpace(j.Id))
                        _ = AcknowledgeCentralJobAsync(j.Id, ok);
                }
            }

            if (e.IsMultipleOf(60)) ProcessPendingRewards();
        }

        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            // F5: 보상 수령 창 열기
            if (e.Button == SButton.F5 && Context.IsPlayerFree)
            {
                OpenClaimMenu();
                Helper.Input.Suppress(e.Button);
                return;
            }

            if (!Context.IsPlayerFree) return;
            bool action = e.Button.IsActionButton();
            bool useTool = e.Button.IsUseToolButton();
            if (!action && !useTool) return;

            // 바닥에 설치된 SOOP 상자를 우클릭하면 다시 줍는다 (예전 버전에서 설치된 상자 회수용).
            if (action && TryPickUpPlacedChest(e.Cursor.GrabTile))
            {
                Helper.Input.Suppress(e.Button);
                return;
            }

            // SOOP 상자를 들고 있으면 바닥에 설치되지 않게 막고, 우클릭이면 연다.
            string? kind = KindOf(Game1.player.CurrentItem);
            if (kind == null) return;
            Helper.Input.Suppress(e.Button);
            if (action) TryOpenHeldChest(kind);
        }

        public static string? KindOf(Item? item) => item?.QualifiedItemId switch
        {
            "(O)" + NormalChestId  => KindNormal,
            "(O)" + PremiumChestId => KindPremium,
            _ => null
        };

        private static string ChestIdOf(string kind) => kind == KindPremium ? PremiumChestId : NormalChestId;
        private static string KindName(string kind) => kind == KindPremium ? "고급 상자" : "일반 상자";

        // ── 플레이어 게임 쪽 ─────────────────────────────────────────────────

        private void OpenClaimMenu()
        {
            if (!Context.IsWorldReady) return;
            if (Game1.activeClickableMenu is RewardClaimMenu) return;

            ClientWaiting = true;
            ClientWaitingSince = DateTime.UtcNow;
            Game1.activeClickableMenu = new RewardClaimMenu(this);
            SendToHost(MsgPendingRequest, new PendingRequestMessage { Reason = "query" });
        }

        public void RequestClaim(string kind)
        {
            if (ClientWaiting) return;

            // 받으려는 종류 중 하나도 인벤토리에 못 들어가면 요청하지 않는다 (보상은 서버 대기열에 그대로 남는다).
            var kinds = kind == KindAll ? new[] { KindNormal, KindPremium } : new[] { kind };
            bool anyFits = kinds.Any(k =>
                (k == KindNormal ? ClientPendingChests : ClientPendingPremiumChests) > 0 &&
                Game1.player.couldInventoryAcceptThisItem(ItemRegistry.Create(ChestIdOf(k), 1)));
            if (!anyFits)
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage("인벤토리에 공간이 없습니다. 한 칸 이상 비운 뒤 다시 수령하세요.", HUDMessage.error_type));
                return;
            }

            ClientWaiting = true;
            ClientWaitingSince = DateTime.UtcNow;
            SendToHost(MsgClaimRequest, new ClaimRequestMessage { Kind = kind });
        }

        private void OnClientPending(PendingMessage msg)
        {
            ClientPendingChests = Math.Max(0, msg.Chests);
            ClientPendingPremiumChests = Math.Max(0, msg.PremiumChests);
            ClientWaiting = false;

            // 접속 직후나 새 보상이 들어왔을 때만 알린다 (창을 열어 조회한 경우는 창에 표시됨).
            if (msg.Reason != "query" && ClientPendingChests + ClientPendingPremiumChests > 0)
                Game1.addHUDMessage(new HUDMessage(
                    $"SOOP 보상 대기 중: 일반 상자 {ClientPendingChests}개, 고급 상자 {ClientPendingPremiumChests}개. F5 또는 /우편함 으로 수령하세요.",
                    HUDMessage.newQuest_type));
        }

        private int AddToInventory(string itemId, int amount)
        {
            if (amount <= 0) return 0;
            Item? left = Game1.player.addItemToInventory(ItemRegistry.Create(itemId, amount));
            return left?.Stack ?? 0;   // 못 넣은 개수
        }

        private void OnClientGrant(GrantMessage msg)
        {
            ClientWaiting = false;
            int gotNormal  = Math.Max(0, msg.Chests);
            int gotPremium = Math.Max(0, msg.PremiumChests);

            int backNormal  = AddToInventory(NormalChestId,  gotNormal);
            int backPremium = AddToInventory(PremiumChestId, gotPremium);
            if (backNormal + backPremium > 0)
                SendToHost(MsgReturn, new ReturnMessage { Chests = backNormal, PremiumChests = backPremium });

            int addedNormal  = gotNormal  - backNormal;
            int addedPremium = gotPremium - backPremium;
            ClientPendingChests        = Math.Max(0, msg.RemainingChests) + backNormal;
            ClientPendingPremiumChests = Math.Max(0, msg.RemainingPremiumChests) + backPremium;

            if (addedNormal + addedPremium > 0)
            {
                var parts = new List<string>();
                if (addedNormal  > 0) parts.Add($"일반 상자 {addedNormal}개");
                if (addedPremium > 0) parts.Add($"고급 상자 {addedPremium}개");
                string text = $"{string.Join(", ", parts)}를 받았습니다.";
                if (backNormal + backPremium > 0) text += $" 공간이 부족해 {backNormal + backPremium}개는 대기 중입니다.";
                Game1.playSound("coin");
                Game1.addHUDMessage(new HUDMessage(text, HUDMessage.newQuest_type));
            }
            else
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage(
                    gotNormal + gotPremium > 0 ? "인벤토리에 공간이 없습니다. 한 칸 이상 비운 뒤 다시 수령하세요." : "수령할 보상이 없습니다.",
                    HUDMessage.error_type));
            }

            if (ClientPendingChests + ClientPendingPremiumChests <= 0 && Game1.activeClickableMenu is RewardClaimMenu menu)
                menu.exitThisMenu();
        }

        // 들고 있는 상자 열기: 호스트에 뽑기를 요청하고, 결과가 오면 상자 1개를 빼고 아이템을 넣는다.
        private void TryOpenHeldChest(string kind)
        {
            if (clientOpening) return;

            Item held = Game1.player.CurrentItem;
            // 상자가 1개뿐이면 열면서 그 칸이 비므로 괜찮다. 여러 개면 빈 칸이 하나는 있어야 한다.
            bool hasEmptySlot = Game1.player.Items.Take(Game1.player.MaxItems).Any(i => i == null);
            if (held.Stack > 1 && !hasEmptySlot)
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage("인벤토리에 공간이 없습니다. 한 칸 이상 비운 뒤 상자를 여세요.", HUDMessage.error_type));
                return;
            }

            clientOpening = true;
            clientOpeningSince = DateTime.UtcNow;
            SendToHost(MsgOpenRequest, new OpenRequestMessage { Kind = kind });
        }

        private void OnClientOpenResult(OpenResultMessage msg)
        {
            clientOpening = false;
            if (!string.IsNullOrEmpty(msg.Error))
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage(msg.Error, HUDMessage.error_type));
                return;
            }

            // 요청 사이에 상자를 다른 칸으로 옮겼을 수 있으므로 인벤토리에서 같은 종류 상자 1개를 찾아 뺀다.
            string chestQid = "(O)" + ChestIdOf(msg.Kind);
            var items = Game1.player.Items;
            int slot = -1;
            if (Game1.player.CurrentItem?.QualifiedItemId == chestQid) slot = Game1.player.CurrentToolIndex;
            else for (int i = 0; i < items.Count; i++) if (items[i]?.QualifiedItemId == chestQid) { slot = i; break; }
            if (slot < 0)
            {
                Game1.addHUDMessage(new HUDMessage("열 상자가 인벤토리에 없습니다.", HUDMessage.error_type));
                return;
            }
            if (items[slot].Stack > 1) items[slot].Stack--; else items[slot] = null;

            string got;
            if (msg.Gold > 0)
            {
                Game1.player.Money += msg.Gold;
                got = $"{msg.Gold:N0}골드";
            }
            else
            {
                Item reward = ItemRegistry.Create(msg.ItemId, Math.Max(1, msg.Amount));
                got = $"{reward.DisplayName} {reward.Stack}개";
                Item? left = Game1.player.addItemToInventory(reward);
                if (left != null && left.Stack > 0)
                {
                    Game1.createItemDebris(left, Game1.player.getStandingPosition(), Game1.player.FacingDirection, Game1.player.currentLocation);
                    got += " (공간이 부족해 일부는 바닥에 떨어졌습니다)";
                }
            }

            Game1.playSound(msg.Kind == KindPremium ? "reward" : "coin");
            Game1.addHUDMessage(new HUDMessage($"{KindName(msg.Kind)}에서 {got}을(를) 얻었습니다!", HUDMessage.newQuest_type));
        }

        // 예전 버전에서 바닥에 설치된 상자를 우클릭하면 인벤토리로 돌려준다.
        private bool TryPickUpPlacedChest(Vector2 tile)
        {
            GameLocation? location = Game1.currentLocation;
            if (location == null || !location.objects.TryGetValue(tile, out StardewValley.Object? placed)) return false;
            string? kind = KindOf(placed);
            if (kind == null) return false;
            if (Vector2.Distance(tile, Game1.player.Tile) > 1.9f) return false;   // 손이 닿는 거리에서만

            Item chest = ItemRegistry.Create(ChestIdOf(kind), Math.Max(1, placed.Stack));
            if (!Game1.player.couldInventoryAcceptThisItem(chest))
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage("인벤토리에 공간이 없어 상자를 주울 수 없습니다.", HUDMessage.error_type));
                return true;
            }

            location.objects.Remove(tile);
            Game1.player.addItemToInventory(chest);
            Game1.playSound("pickUpItem");
            Game1.addHUDMessage(new HUDMessage($"설치된 {KindName(kind)}를 다시 주웠습니다. 들고 우클릭하면 열 수 있습니다.", HUDMessage.newQuest_type));
            return true;
        }

        // ── 호스트 쪽 ────────────────────────────────────────────────────────

        private void SendPending(long farmerId, string reason)
        {
            State.PendingChests.TryGetValue(farmerId, out int normal);
            State.PendingPremiumChests.TryGetValue(farmerId, out int premium);
            SendToPlayer(farmerId, MsgPending, new PendingMessage { Chests = normal, PremiumChests = premium, Reason = reason });
        }

        private void OnHostPendingRequest(long fromId, PendingRequestMessage msg)
            => SendPending(fromId, msg.Reason == "login" ? "login" : "query");

        private static int Take(Dictionary<long, int> dict, long farmerId, int max)
        {
            dict.TryGetValue(farmerId, out int pending);
            int give = Math.Min(Math.Max(0, pending), max);
            int remaining = pending - give;
            if (remaining > 0) dict[farmerId] = remaining; else dict.Remove(farmerId);
            return give;
        }

        private void OnHostClaimRequest(long fromId, ClaimRequestMessage msg)
        {
            int normal  = msg.Kind is KindNormal  or KindAll ? Take(State.PendingChests,        fromId, MaxGrantPerClaim) : 0;
            int premium = msg.Kind is KindPremium or KindAll ? Take(State.PendingPremiumChests, fromId, MaxGrantPerClaim) : 0;

            if (normal + premium > 0)
            {
                lastGrant[fromId] = (normal, premium);
                SaveState();
                Monitor.Log($"보상 상자 지급: FarmerId={fromId}, 일반={normal}, 고급={premium}", LogLevel.Info);
            }

            State.PendingChests.TryGetValue(fromId, out int leftNormal);
            State.PendingPremiumChests.TryGetValue(fromId, out int leftPremium);
            SendToPlayer(fromId, MsgGrant, new GrantMessage { Chests = normal, PremiumChests = premium, RemainingChests = leftNormal, RemainingPremiumChests = leftPremium });
        }

        private void OnHostReturn(long fromId, ReturnMessage msg)
        {
            lastGrant.TryGetValue(fromId, out var granted);
            lastGrant.Remove(fromId);
            // 방금 보낸 양보다 많이 돌려받지 않는다.
            int backNormal  = Math.Min(Math.Max(0, msg.Chests),        granted.Chests);
            int backPremium = Math.Min(Math.Max(0, msg.PremiumChests), granted.Premium);
            if (backNormal + backPremium <= 0) return;

            AddPending(State.PendingChests,        fromId, backNormal);
            AddPending(State.PendingPremiumChests, fromId, backPremium);
            SaveState();
            Monitor.Log($"보상 상자 반환(인벤토리 부족): FarmerId={fromId}, 일반={backNormal}, 고급={backPremium}", LogLevel.Info);
        }

        private void OnHostOpenRequest(long fromId, OpenRequestMessage msg)
        {
            string kind = msg.Kind == KindPremium ? KindPremium : KindNormal;
            LootEntry? entry = RollLoot(kind);
            if (entry == null)
            {
                SendToPlayer(fromId, MsgOpenResult, new OpenResultMessage { Kind = kind, Error = "상자 아이템 목록(loot.json)이 비어 있거나 잘못되었습니다. 서버 로그를 확인하세요." });
                return;
            }

            Monitor.Log($"상자 열기: FarmerId={fromId}, {KindName(kind)} → " + (entry.Gold > 0 ? $"{entry.Gold}골드" : $"{entry.ItemId} {entry.Amount}개"), LogLevel.Info);
            SendToPlayer(fromId, MsgOpenResult, new OpenResultMessage { Kind = kind, ItemId = entry.ItemId ?? "", Amount = entry.Amount, Gold = entry.Gold });
        }

        // 새 상자가 대기열에 들어오면 접속 중인 해당 플레이어에게 알린다.
        private void NotifyPending(long farmerId)
        {
            if (FindOnlineFarmer(farmerId) != null) SendPending(farmerId, "notify");
        }

        // ── 메시지 송수신 ────────────────────────────────────────────────────

        private void SendToHost<T>(string type, T message) where T : notnull
        {
            if (Context.IsMainPlayer)
            {
                // 호스트가 직접 플레이하는 경우 (전용 서버가 아닐 때)
                HandleHostMessage(type, Game1.player.UniqueMultiplayerID, message);
                return;
            }
            Helper.Multiplayer.SendMessage(message, type,
                modIDs: new[] { ModManifest.UniqueID },
                playerIDs: new[] { Game1.MasterPlayer.UniqueMultiplayerID });
        }

        private void SendToPlayer<T>(long farmerId, string type, T message) where T : notnull
        {
            if (farmerId == Game1.player.UniqueMultiplayerID)
            {
                HandleClientMessage(type, message);
                return;
            }
            Helper.Multiplayer.SendMessage(message, type,
                modIDs: new[] { ModManifest.UniqueID },
                playerIDs: new[] { farmerId });
        }

        private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
        {
            if (e.FromModID != ModManifest.UniqueID) return;

            try
            {
                if (Context.IsMainPlayer)
                {
                    if (FindOnlineFarmer(e.FromPlayerID) == null) return;   // 접속 중인 플레이어만
                    switch (e.Type)
                    {
                        case MsgPendingRequest: HandleHostMessage(e.Type, e.FromPlayerID, e.ReadAs<PendingRequestMessage>()); break;
                        case MsgClaimRequest:   HandleHostMessage(e.Type, e.FromPlayerID, e.ReadAs<ClaimRequestMessage>());   break;
                        case MsgReturn:         HandleHostMessage(e.Type, e.FromPlayerID, e.ReadAs<ReturnMessage>());         break;
                        case MsgOpenRequest:    HandleHostMessage(e.Type, e.FromPlayerID, e.ReadAs<OpenRequestMessage>());    break;
                        case MsgLinkRequest:    HandleHostMessage(e.Type, e.FromPlayerID, e.ReadAs<LinkRequestMessage>());    break;
                        case MsgUnlinkRequest:  HandleHostMessage(e.Type, e.FromPlayerID, e.ReadAs<UnlinkRequestMessage>());  break;
                    }
                }
                else
                {
                    if (e.FromPlayerID != Game1.MasterPlayer.UniqueMultiplayerID) return;   // 호스트가 보낸 것만
                    switch (e.Type)
                    {
                        case MsgPending:    HandleClientMessage(e.Type, e.ReadAs<PendingMessage>());    break;
                        case MsgGrant:      HandleClientMessage(e.Type, e.ReadAs<GrantMessage>());      break;
                        case MsgOpenResult: HandleClientMessage(e.Type, e.ReadAs<OpenResultMessage>()); break;
                        case MsgLinkResponse:   HandleClientMessage(e.Type, e.ReadAs<LinkResponseMessage>());   break;
                        case MsgUnlinkResponse: HandleClientMessage(e.Type, e.ReadAs<UnlinkResponseMessage>()); break;
                    }
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"모드 메시지 처리 오류 ({e.Type}): {ex.Message}", LogLevel.Warn);
            }
        }

        private void HandleHostMessage(string type, long fromId, object message)
        {
            switch (message)
            {
                case PendingRequestMessage m when type == MsgPendingRequest: OnHostPendingRequest(fromId, m); break;
                case ClaimRequestMessage m   when type == MsgClaimRequest:   OnHostClaimRequest(fromId, m);   break;
                case ReturnMessage m         when type == MsgReturn:         OnHostReturn(fromId, m);         break;
                case OpenRequestMessage m    when type == MsgOpenRequest:    OnHostOpenRequest(fromId, m);    break;
                case LinkRequestMessage      when type == MsgLinkRequest:    OnHostLinkRequest(fromId);       break;
                case UnlinkRequestMessage    when type == MsgUnlinkRequest:  OnHostUnlinkRequest(fromId);     break;
            }
        }

        private void HandleClientMessage(string type, object message)
        {
            switch (message)
            {
                case PendingMessage m    when type == MsgPending:    OnClientPending(m);    break;
                case GrantMessage m      when type == MsgGrant:      OnClientGrant(m);      break;
                case OpenResultMessage m when type == MsgOpenResult: OnClientOpenResult(m); break;
                case LinkResponseMessage m   when type == MsgLinkResponse:   OnClientLinkResponse(m);   break;
                case UnlinkResponseMessage m when type == MsgUnlinkResponse: OnClientUnlinkResponse(m); break;
            }
        }

        // ── SOOP 계정 연결 (/숲로그인, /숲로그아웃) ──────────────────────────
        // 1) 플레이어 게임이 호스트에 요청 → 2) 호스트가 Central에서 로그인 주소와 세션 키를 받음
        // 3) 플레이어 PC의 기본 브라우저로 SOOP 로그인 → 4) 플레이어 PC에서 숨은 Edge가 Central 연결 페이지를 열어 SDK 연결
        // 숨은 Edge는 게임이 켜져 있는 동안만 동작한다. 게임을 끄거나 타이틀로 나가면 끈다.

        private static void ChatInfo(string text)
        {
            if (Game1.chatBox != null) Game1.chatBox.addInfoMessage(text);
            else Game1.addHUDMessage(new HUDMessage(text, HUDMessage.newQuest_type));
        }

        private static void ChatError(string text)
        {
            if (Game1.chatBox != null) Game1.chatBox.addErrorMessage(text);
            else Game1.addHUDMessage(new HUDMessage(text, HUDMessage.error_type));
        }

        private static string LinkFileFor(long farmerId) => $"sessions/{farmerId}.json";

        private void StartSoopLogin()
        {
            if (!Context.IsWorldReady) return;
            if (!OperatingSystem.IsWindows()) { ChatError("SOOP 연결은 Windows에서만 지원합니다."); return; }
            if (FindEdge() == null) { ChatError("Microsoft Edge를 찾지 못했습니다. 모드 config.json의 EdgePath에 msedge.exe 경로를 적어 주세요."); return; }
            if (clientLinkPending) { ChatInfo("SOOP 로그인을 준비하는 중입니다. 잠시만 기다려 주세요."); return; }

            clientLinkPending = true;
            ChatInfo("SOOP 로그인을 준비하는 중입니다...");
            SendToHost(MsgLinkRequest, new LinkRequestMessage());
        }

        private void OnClientLinkResponse(LinkResponseMessage msg)
        {
            clientLinkPending = false;
            if (!string.IsNullOrEmpty(msg.Error) || string.IsNullOrEmpty(msg.SessionKey))
            {
                ChatError($"SOOP 로그인을 시작하지 못했습니다: {msg.Error}");
                return;
            }

            var saved = new SavedLink { PageUrl = msg.PageUrl, SessionKey = msg.SessionKey, SavedAt = DateTime.UtcNow.ToString("o") };
            Helper.Data.WriteJsonFile(LinkFileFor(Game1.player.UniqueMultiplayerID), saved);
            if (!StartEdge(saved)) return;

            try
            {
                Process.Start(new ProcessStartInfo(msg.LoginUrl) { UseShellExecute = true });
                ChatInfo("브라우저에서 SOOP 로그인을 마치면 내 방송의 후원을 받기 시작합니다.");
            }
            catch (Exception ex)
            {
                Monitor.Log($"기본 브라우저 열기 실패: {ex.Message}", LogLevel.Warn);
                ChatError($"브라우저를 열지 못했습니다. 이 주소를 직접 열어 주세요: {msg.LoginUrl}");
            }
        }

        // 전에 /숲로그인 했으면 접속할 때 숨은 Edge를 다시 띄운다 (SOOP 로그인이 유효하면 바로 연결된다).
        private void ResumeSavedLink()
        {
            if (!OperatingSystem.IsWindows()) return;
            SavedLink? saved = Helper.Data.ReadJsonFile<SavedLink>(LinkFileFor(Game1.player.UniqueMultiplayerID));
            if (saved == null || string.IsNullOrEmpty(saved.SessionKey)) return;
            if (StartEdge(saved))
                ChatInfo("SOOP 방송 연결을 다시 시작합니다. (해제하려면 /숲로그아웃)");
        }

        private void StartSoopLogout()
        {
            if (!Context.IsWorldReady) return;
            StopEdge("/숲로그아웃");
            Helper.Data.WriteJsonFile<SavedLink>(LinkFileFor(Game1.player.UniqueMultiplayerID), null);   // 이 PC에 저장한 세션 키 삭제
            ChatInfo("SOOP 연결을 해제하는 중입니다...");
            SendToHost(MsgUnlinkRequest, new UnlinkRequestMessage());
        }

        private void OnClientUnlinkResponse(UnlinkResponseMessage msg)
        {
            if (msg.Ok) ChatInfo("SOOP 연결을 해제하고 연결 정보를 삭제했습니다.");
            else ChatError($"관리자 서버의 연결 정보 삭제에 실패했습니다: {msg.Error} (이 PC의 연결은 이미 끊었습니다)");
        }

        // 호스트: 플레이어 대신 Central에 요청한다 (서버 토큰은 호스트에만 있다).
        private void OnHostLinkRequest(long fromId)
        {
            Farmer? farmer = FindOnlineFarmer(fromId);
            BridgeConfig cfg = config;
            if (string.IsNullOrWhiteSpace(cfg.CentralServerUrl) || string.IsNullOrWhiteSpace(cfg.AgentToken))
            {
                SendToPlayer(fromId, MsgLinkResponse, new LinkResponseMessage { Error = "게임 서버에 관리자 서버(Central) 연결이 설정되지 않았습니다." });
                return;
            }

            string farmerName = farmer?.Name ?? "";
            Monitor.Log($"/숲로그인 요청: {farmerName} (FarmerId={fromId})", LogLevel.Info);
            _ = Task.Run(async () =>
            {
                LinkResponseMessage reply;
                try
                {
                    var (ok, json, error) = await CentralPostAsync(cfg, "/api/agent/link/start", new { farmerId = fromId.ToString(), farmerName }).ConfigureAwait(false);
                    reply = ok
                        ? new LinkResponseMessage { LoginUrl = Str(json, "loginUrl"), PageUrl = Str(json, "pageUrl"), SessionKey = Str(json, "sessionKey") }
                        : new LinkResponseMessage { Error = error };
                }
                catch (Exception ex)
                {
                    reply = new LinkResponseMessage { Error = $"관리자 서버에 연결하지 못했습니다: {ex.Message}" };
                }
                hostMainThreadActions.Enqueue(() => SendToPlayer(fromId, MsgLinkResponse, reply));
            });
        }

        private void OnHostUnlinkRequest(long fromId)
        {
            BridgeConfig cfg = config;
            if (string.IsNullOrWhiteSpace(cfg.CentralServerUrl) || string.IsNullOrWhiteSpace(cfg.AgentToken))
            {
                SendToPlayer(fromId, MsgUnlinkResponse, new UnlinkResponseMessage { Error = "게임 서버에 관리자 서버(Central) 연결이 설정되지 않았습니다." });
                return;
            }

            Monitor.Log($"/숲로그아웃 요청: FarmerId={fromId}", LogLevel.Info);
            _ = Task.Run(async () =>
            {
                UnlinkResponseMessage reply;
                try
                {
                    var (ok, _, error) = await CentralPostAsync(cfg, "/api/agent/link/remove", new { farmerId = fromId.ToString() }).ConfigureAwait(false);
                    reply = new UnlinkResponseMessage { Ok = ok, Error = error };
                }
                catch (Exception ex)
                {
                    reply = new UnlinkResponseMessage { Error = $"관리자 서버에 연결하지 못했습니다: {ex.Message}" };
                }
                hostMainThreadActions.Enqueue(() => SendToPlayer(fromId, MsgUnlinkResponse, reply));
            });
        }

        private async Task<(bool ok, JsonElement json, string error)> CentralPostAsync(BridgeConfig cfg, string path, object body)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, cfg.CentralServerUrl.TrimEnd('/') + path);
            req.Headers.Add("X-Agent-Token", cfg.AgentToken);
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using HttpResponseMessage resp = await centralHttp.SendAsync(req).ConfigureAwait(false);
            string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            JsonElement json = default;
            try { json = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text).RootElement.Clone(); } catch (JsonException) { }
            if (resp.IsSuccessStatusCode) return (true, json, "");
            string error = Str(json, "error");
            return (false, json, string.IsNullOrEmpty(error) ? $"HTTP {(int)resp.StatusCode}" : error);
        }

        private static string Str(JsonElement json, string name)
            => json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        // ── 숨은 Edge 실행/종료 (플레이어 PC) ────────────────────────────────

        private string EdgeProfileDir => Path.Combine(Helper.DirectoryPath, "edge-profile");   // 평소 브라우저와 분리된 전용 저장 공간
        private string EdgePidFile    => Path.Combine(Helper.DirectoryPath, "edge.pid");

        private string? FindEdge()
        {
            if (!OperatingSystem.IsWindows()) return null;
            string custom = config.EdgePath;
            if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom)) return custom;
            foreach (string baseDir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            {
                if (string.IsNullOrEmpty(baseDir)) continue;
                string path = Path.Combine(baseDir, "Microsoft", "Edge", "Application", "msedge.exe");
                if (File.Exists(path)) return path;
            }
            return null;
        }

        private bool StartEdge(SavedLink link)
        {
            StopEdge("새 연결 시작");
            string? exe = FindEdge();
            if (exe == null)
            {
                ChatError("Microsoft Edge를 찾지 못했습니다. 모드 config.json의 EdgePath에 msedge.exe 경로를 적어 주세요.");
                return false;
            }

            // 세션 키는 # 뒤에 둔다. 브라우저가 서버로 보내지 않아 서버 로그에 남지 않는다.
            string url = $"{link.PageUrl}#k={Uri.EscapeDataString(link.SessionKey)}";
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            bool headless = config.EdgeHeadless;
            if (headless) psi.ArgumentList.Add("--headless=new");
            psi.ArgumentList.Add($"--user-data-dir={EdgeProfileDir}");
            foreach (string flag in new[] { "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--mute-audio",
                                             "--disable-background-timer-throttling", "--disable-renderer-backgrounding", "--disable-backgrounding-occluded-windows" })
                psi.ArgumentList.Add(flag);
            if (headless) psi.ArgumentList.Add(url);
            else { psi.ArgumentList.Add("--window-size=560,440"); psi.ArgumentList.Add($"--app={url}"); }

            try
            {
                edgeProcess = Process.Start(psi);
                if (edgeProcess == null) throw new InvalidOperationException("프로세스를 시작하지 못했습니다.");
                File.WriteAllText(EdgePidFile, $"{edgeProcess.Id}|{edgeProcess.StartTime.ToUniversalTime().Ticks}");
                Monitor.Log($"숨은 Edge 시작 (PID {edgeProcess.Id}, {(headless ? "화면 없음" : "창 모드")})", LogLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                Monitor.Log($"Edge 실행 실패: {ex.Message}", LogLevel.Error);
                ChatError($"SOOP 연결용 Edge를 실행하지 못했습니다: {ex.Message}");
                return false;
            }
        }

        private void StopEdge(string reason)
        {
            System.Diagnostics.Process? p = edgeProcess;
            edgeProcess = null;
            try
            {
                if (p != null && !p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                    Monitor.Log($"숨은 Edge 종료: {reason}", LogLevel.Info);
                }
            }
            catch (Exception ex) { Monitor.Log($"숨은 Edge 종료 실패: {ex.Message}", LogLevel.Warn); }
            try { if (File.Exists(EdgePidFile)) File.Delete(EdgePidFile); } catch { }
        }

        // 게임이 튕기면 Edge를 끌 기회가 없다. 다음에 게임을 켤 때 기록해 둔 그 Edge만 골라서 끈다.
        private void CleanupOrphanEdge()
        {
            try
            {
                if (!File.Exists(EdgePidFile)) return;
                string[] parts = File.ReadAllText(EdgePidFile).Split('|');
                if (parts.Length == 2 && int.TryParse(parts[0], out int pid) && long.TryParse(parts[1], out long ticks))
                {
                    try
                    {
                        using System.Diagnostics.Process p = System.Diagnostics.Process.GetProcessById(pid);
                        if (p.StartTime.ToUniversalTime().Ticks == ticks)   // PID가 다른 프로그램에 재사용된 경우는 건드리지 않는다
                        {
                            p.Kill(entireProcessTree: true);
                            Monitor.Log("지난번에 남은 숨은 Edge를 종료했습니다.", LogLevel.Info);
                        }
                    }
                    catch (ArgumentException) { /* 이미 종료됨 */ }
                }
                File.Delete(EdgePidFile);
            }
            catch (Exception ex) { Monitor.Log($"남은 Edge 정리 실패: {ex.Message}", LogLevel.Trace); }
        }

        // ── 몬스터 자동 소환 (호스트, 접속 중 플레이어) ──────────────────────
        // 상자는 플레이어가 창에서 직접 수령한다.

        private void ProcessPendingRewards()
        {
            if (!Context.IsWorldReady || !Context.IsMainPlayer) return;

            foreach (var entry in State.PendingMonsters.ToArray())
            {
                long farmerId = entry.Key;
                if (entry.Value <= 0) { State.PendingMonsters.Remove(farmerId); SaveState(); continue; }

                Farmer? farmer = FindOnlineFarmer(farmerId);
                if (farmer?.currentLocation == null) continue;

                var spawnedNames = new List<string>();
                for (int i = 0; i < entry.Value; i++)
                {
                    Vector2 tile  = farmer.Tile;
                    Vector2 spawn = new(tile.X + random.Next(-3, 4), tile.Y + random.Next(-3, 4));
                    MonsterEntry pick = RollMonster();
                    farmer.currentLocation.characters.Add(CreateMonster(pick.Type, spawn * 64f));
                    spawnedNames.Add(string.IsNullOrWhiteSpace(pick.Name) ? pick.Type : pick.Name);
                    AnnounceRareMonster(farmer, pick);
                }
                State.PendingMonsters.Remove(farmerId);
                Monitor.Log($"몬스터 소환 완료: FarmerId={farmerId}, 수량={entry.Value}, 종류={string.Join(", ", spawnedNames)}", LogLevel.Info);
                SaveState();
            }
        }

        // ── 에셋: 상자 2종 등록 ─────────────────────────────────────────────
        // Category 0 = 일반 물건. 제작 분류(-8)는 게임이 바닥 설치를 허용하므로 쓰지 않는다.

        private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
        {
            if (e.NameWithoutLocale.IsEquivalentTo(NormalTextureAsset))
            {
                e.LoadFromModFile<Texture2D>("assets/reward_chest.png", AssetLoadPriority.Exclusive);
                return;
            }
            if (e.NameWithoutLocale.IsEquivalentTo(PremiumTextureAsset))
            {
                e.LoadFromModFile<Texture2D>("assets/premium_chest.png", AssetLoadPriority.Exclusive);
                return;
            }

            if (e.NameWithoutLocale.IsEquivalentTo("Data/Objects"))
            {
                e.Edit(asset =>
                {
                    var data = asset.AsDictionary<string, ObjectData>().Data;
                    data[NormalChestId] = new ObjectData
                    {
                        Name = NormalChestId, DisplayName = "SOOP 일반 상자",
                        Description = "SOOP 후원으로 받은 상자. 들고 우클릭하면 열립니다.",
                        Type = "Basic", Category = 0, Price = 0,
                        Texture = NormalTextureAsset, SpriteIndex = 0
                    };
                    data[PremiumChestId] = new ObjectData
                    {
                        Name = PremiumChestId, DisplayName = "SOOP 고급 상자",
                        Description = "SOOP 후원으로 받은 고급 상자. 들고 우클릭하면 열립니다.",
                        Type = "Basic", Category = 0, Price = 0,
                        Texture = PremiumTextureAsset, SpriteIndex = 0
                    };
                });
            }
        }

        // ── 감사 로그 ────────────────────────────────────────────────────────

        private void LogOnlineFarmers()
        {
            foreach (Farmer farmer in Game1.getOnlineFarmers())
            {
                long fid = farmer.UniqueMultiplayerID;
                if (!loggedFarmerIds.Add(fid)) continue;
                Monitor.Log($"접속 플레이어 확인: Name={farmer.Name}, FarmerId={fid}", LogLevel.Info);
            }
        }

        // ── Central 서버 폴링 ────────────────────────────────────────────────

        private void StartCentralPolling()
        {
            centralCancellation = new CancellationTokenSource();
            _ = CentralPollingLoopAsync(centralCancellation.Token);
        }

        private async Task CentralPollingLoopAsync(CancellationToken ct)
        {
            string announced = "";
            while (!ct.IsCancellationRequested)
            {
                // 매 루프마다 최신 config를 읽는다. CentralServerUrl/AgentToken/PollIntervalSeconds를 고치면 바로 반영된다.
                BridgeConfig cfg = config;

                if (string.IsNullOrWhiteSpace(cfg.CentralServerUrl) || string.IsNullOrWhiteSpace(cfg.AgentToken))
                {
                    announced = "";
                    try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                string baseUrl = cfg.CentralServerUrl.TrimEnd('/');
                if (announced != baseUrl)
                {
                    Monitor.Log($"Central 서버 폴링 중: {baseUrl}", LogLevel.Info);
                    announced = baseUrl;
                }

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/agent/poll");
                    req.Headers.Add("X-Agent-Token", cfg.AgentToken);
                    // 지금 접속 중인 플레이어 목록을 같이 보낸다. Central은 게임에 없는 스트리머의 SOOP 연결을 끊는다.
                    req.Content = new StringContent(JsonSerializer.Serialize(new { onlineFarmerIds = onlineFarmerSnapshot }), Encoding.UTF8, "application/json");
                    using HttpResponseMessage resp = await centralHttp.SendAsync(req, ct).ConfigureAwait(false);
                    string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        var poll = JsonSerializer.Deserialize<CentralPollResponse>(json, JsonOptions);
                        if (poll?.Job != null) incomingCentralJobs.Enqueue(poll.Job);
                    }
                    else if ((int)resp.StatusCode == 401)
                    {
                        Monitor.Log("Central 서버 인증 실패: AgentToken 또는 방송 활성 상태를 확인하세요.", LogLevel.Error);
                        await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                        continue;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { Monitor.Log($"Central 연결 오류(재시도 예정): {ex.Message}", LogLevel.Trace); }

                try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, cfg.PollIntervalSeconds)), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task AcknowledgeCentralJobAsync(string jobId, bool processed)
        {
            try
            {
                string baseUrl = config.CentralServerUrl.TrimEnd('/');
                using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/agent/ack");
                req.Headers.Add("X-Agent-Token", config.AgentToken);
                req.Content = new StringContent(JsonSerializer.Serialize(new
                {
                    jobId,
                    status  = processed ? "COMPLETED" : "REVIEW",
                    message = processed ? "SMAPI가 보상을 처리했습니다." : "FarmerId가 없거나 플레이어가 오프라인 상태입니다."
                }), Encoding.UTF8, "application/json");
                using HttpResponseMessage resp = await centralHttp.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    Monitor.Log($"Central ack 전송 실패: job={jobId}, HTTP={(int)resp.StatusCode}", LogLevel.Warn);
            }
            catch (Exception ex) { Monitor.Log($"Central ack 오류: job={jobId}, {ex.Message}", LogLevel.Warn); }
        }

        // ── 로컬 HTTP ────────────────────────────────────────────────────────

        private void StartHttpListener()
        {
            if (config.Port < 1 || config.Port > 65535)
            { Monitor.Log("config.json의 Port 값이 잘못되었습니다.", LogLevel.Error); return; }

            listenerCancellation = new CancellationTokenSource();
            listener = new HttpListener();
            listener.Prefixes.Add($"http://+:{config.Port}/");
            try
            {
                listener.Start();
                _ = ListenLoopAsync(listener, listenerCancellation.Token);
            }
            catch (Exception ex)
            { Monitor.Log($"HTTP 수신부 시작 실패: {ex}", LogLevel.Error); listener.Close(); listener = null; }
        }

        private async Task ListenLoopAsync(HttpListener httpListener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && httpListener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await httpListener.GetContextAsync().ConfigureAwait(false); }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) { Monitor.Log($"HTTP 수신 오류: {ex.Message}", LogLevel.Warn); continue; }
                _ = HandleHttpRequestAsync(ctx);
            }
        }

        private async Task HandleHttpRequestAsync(HttpListenerContext context)
        {
            try
            {
                var req  = context.Request;
                var resp = context.Response;

                if (!string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                { await WriteResponseAsync(resp, 405, "{\"error\":\"POST required\"}"); return; }

                if (!string.Equals(req.Url?.AbsolutePath, "/event", StringComparison.OrdinalIgnoreCase))
                { await WriteResponseAsync(resp, 404, "{\"error\":\"not found\"}"); return; }

                if (!FixedTimeEquals(req.Headers["X-SOOP-Token"] ?? "", config.AuthToken))
                { await WriteResponseAsync(resp, 401, "{\"error\":\"unauthorized\"}"); return; }

                if (req.ContentLength64 > 64 * 1024)
                { await WriteResponseAsync(resp, 413, "{\"error\":\"payload too large\"}"); return; }

                string body;
                using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
                    body = await reader.ReadToEndAsync().ConfigureAwait(false);

                DonationEvent? donation;
                try { donation = JsonSerializer.Deserialize<DonationEvent>(body, JsonOptions); }
                catch (JsonException) { await WriteResponseAsync(resp, 400, "{\"error\":\"invalid json\"}"); return; }

                if (donation == null || string.IsNullOrWhiteSpace(donation.EventId) ||
                    string.IsNullOrWhiteSpace(donation.SoopId) || donation.Balloons <= 0)
                { await WriteResponseAsync(resp, 400, "{\"error\":\"eventId, soopId and positive balloons are required\"}"); return; }

                incomingEvents.Enqueue(donation);
                await WriteResponseAsync(resp, 202, "{\"accepted\":true}");
            }
            catch (Exception ex)
            {
                Monitor.Log($"HTTP 요청 처리 오류: {ex.Message}", LogLevel.Warn);
                try { await WriteResponseAsync(context.Response, 500, "{\"error\":\"internal error\"}"); } catch { }
            }
        }

        private static async Task WriteResponseAsync(HttpListenerResponse r, int code, string body)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            r.StatusCode = code; r.ContentType = "application/json; charset=utf-8";
            r.ContentLength64 = bytes.Length;
            await r.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            r.Close();
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;
            int d = 0; for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i]; return d == 0;
        }

        // ── 보상 로직 ────────────────────────────────────────────────────────

        // 로컬 /event 경로 (브리지에 Central 설정이 없을 때만 쓰인다).
        private void ProcessDonation(DonationEvent donation)
        {
            string eventId = donation.EventId.Trim();
            string soopId  = donation.SoopId.Trim();

            if (State.ProcessedEventIds.Contains(eventId))
            { Monitor.Log($"중복 이벤트 무시: {eventId}", LogLevel.Warn); return; }

            if (donation.Balloons <= 0)
            { Monitor.Log($"잘못된 후원 수량: {soopId}", LogLevel.Warn); return; }

            long? targetFarmerId = donation.FarmerId > 0 ? donation.FarmerId : null;
            if (targetFarmerId == null)
            { Monitor.Log($"FarmerId 없음 (soopId={soopId})", LogLevel.Warn); RememberEvent(eventId); return; }

            int balloons = donation.Balloons;
            var (normal, premium, monsters) = ComputeRewards(rules, balloons);   // rules.json 기준

            if (normal + premium + monsters == 0)
            { RememberEvent(eventId); Monitor.Log($"보상 기준 미달: {soopId}, {balloons}개", LogLevel.Info); return; }

            AddPending(State.PendingChests,        targetFarmerId.Value, normal);
            AddPending(State.PendingPremiumChests, targetFarmerId.Value, premium);
            AddPending(State.PendingMonsters,      targetFarmerId.Value, monsters);
            RememberEvent(eventId);

            Monitor.Log($"후원 이벤트 접수: {eventId}, {soopId}, {balloons}개 → 일반:{normal} 고급:{premium} 몬스터:{monsters}", LogLevel.Info);
            if (normal + premium > 0) NotifyPending(targetFarmerId.Value);
            ProcessPendingRewards();
        }

        private bool ProcessCentralJob(CentralJob job)
        {
            string jobKey = "central:" + job.Id.Trim();
            if (State.ProcessedEventIds.Contains(jobKey))
            { Monitor.Log($"Central 중복 무시: {job.Id}", LogLevel.Warn); return true; }

            long? fid = job.GetFarmerIdLong();
            if (fid == null || fid <= 0)
            { Monitor.Log($"Central FarmerId 없음: {job.Id}", LogLevel.Warn); return false; }

            switch (job.RewardType)
            {
                case KindNormal:  AddPending(State.PendingChests,        fid.Value, job.Amount); break;
                case KindPremium: AddPending(State.PendingPremiumChests, fid.Value, job.Amount); break;
                case "monster":   AddPending(State.PendingMonsters,      fid.Value, job.Amount); break;
                default: Monitor.Log($"알 수 없는 보상 종류: {job.RewardType}", LogLevel.Warn); return false;
            }

            RememberEvent(jobKey);
            Monitor.Log($"Central 보상 접수: {job.Id}, {job.SoopId}, {job.RewardType} {job.Amount}개, FarmerId={fid}", LogLevel.Info);
            if (job.RewardType is KindNormal or KindPremium) NotifyPending(fid.Value);
            ProcessPendingRewards();
            return true;
        }

        private void RememberEvent(string id)
        {
            State.ProcessedEventIds.Add(id);
            while (State.ProcessedEventIds.Count > 50000) State.ProcessedEventIds.RemoveAt(0);
            SaveState();
        }

        private static void AddPending(Dictionary<long, int> dict, long fid, int count)
        {
            if (count <= 0) return;
            dict.TryGetValue(fid, out int ex);
            dict[fid] = ex + count;
        }

        private static Farmer? FindOnlineFarmer(long fid)
            => Game1.getOnlineFarmers().FirstOrDefault(f => f.UniqueMultiplayerID == fid);

        public void SaveState() => Helper.Data.WriteJsonFile("state.json", State);

        // ── 설정 자동 재로드 (config.json / rules.json) ──────────────────────
        // DLL을 바꾸지 않아도 두 파일을 수정하면 2초 안에 반영된다.
        // 파일이 잘못되어 있으면 오류를 로그에 남기고 이전 값을 그대로 쓴다.

        private DateTime StampOf(string fileName)
        {
            string path = Path.Combine(Helper.DirectoryPath, fileName);
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }

        private void HotReload()
        {
            try
            {
                ReloadConfig();
                ReloadRules(force: false);
                ReloadLoot(force: false);
                ReloadMonsters(force: false);
            }
            catch (Exception ex)
            {
                Monitor.Log($"설정 자동 재로드 오류: {ex.Message}", LogLevel.Warn);
            }
        }

        private void ReloadConfig()
        {
            DateTime stamp = StampOf(ConfigFileName);
            if (stamp == configWriteUtc || stamp == DateTime.MinValue) return;
            configWriteUtc = stamp;

            try
            {
                // Helper.ReadConfig는 읽을 때마다 파일을 다시 써서 수정 시각이 바뀌므로
                // (그러면 자기 자신을 변경으로 감지해 무한 반복된다) 쓰기 없는 ReadJsonFile을 쓴다.
                BridgeConfig? loaded = Helper.Data.ReadJsonFile<BridgeConfig>(ConfigFileName);
                if (loaded == null)
                {
                    Monitor.Log("config.json이 비어 있어 이전 설정을 유지합니다.", LogLevel.Error);
                    return;
                }
                BridgeConfig current = config;

                if (string.IsNullOrWhiteSpace(loaded.AuthToken))
                    loaded.AuthToken = current.AuthToken;   // 토큰을 비워도 기존 값을 유지한다.
                if (loaded.Port != current.Port)
                {
                    Monitor.Log($"Port 변경({current.Port} → {loaded.Port})은 서버 재시작 후 적용됩니다. 지금은 {current.Port}를 계속 사용합니다.", LogLevel.Warn);
                    loaded.Port = current.Port;
                }

                config = loaded;
                Monitor.Log("config.json 변경을 반영했습니다.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Monitor.Log($"config.json 오류, 이전 설정을 유지합니다: {ex.Message}", LogLevel.Error);
            }
        }

        private void EnsureRulesFile()
        {
            if (File.Exists(Path.Combine(Helper.DirectoryPath, RulesFileName))) return;
            Helper.Data.WriteJsonFile(RulesFileName, RulesFile.CreateDefault());
            Monitor.Log("rules.json이 없어 기본 규칙으로 만들었습니다.", LogLevel.Info);
        }

        private void ReloadRules(bool force)
        {
            DateTime stamp = StampOf(RulesFileName);
            if (stamp == DateTime.MinValue) return;
            if (!force && stamp == rulesWriteUtc) return;
            rulesWriteUtc = stamp;

            try
            {
                RulesFile? loaded = Helper.Data.ReadJsonFile<RulesFile>(RulesFileName);
                if (loaded == null)
                {
                    Monitor.Log("rules.json이 비어 있어 이전 규칙을 유지합니다.", LogLevel.Error);
                    return;
                }

                string? error = ValidateRules(loaded);
                if (error != null)
                {
                    Monitor.Log($"rules.json 오류, 이전 규칙을 유지합니다: {error}", LogLevel.Error);
                    return;
                }

                rules = loaded;
                Monitor.Log($"보상 규칙을 반영했습니다: {loaded.Rules.Count}개", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Monitor.Log($"rules.json을 읽지 못해 이전 규칙을 유지합니다: {ex.Message}", LogLevel.Error);
            }
        }

        // 규칙 형식은 Central의 config/rules.json과 같다.
        private static string? ValidateRules(RulesFile file)
        {
            if (file.Rules == null || file.Rules.Count == 0)
                return "rules 배열이 비어 있습니다.";

            for (int i = 0; i < file.Rules.Count; i++)
            {
                RewardRule r = file.Rules[i];
                string at = $"rules[{i}]";

                if (r.Type != KindNormal && r.Type != KindPremium && r.Type != "monster") return $"{at}: type은 chest, premiumChest, monster 중 하나여야 합니다.";
                if (r.MinBalloons   is < 1) return $"{at}: minBalloons는 1 이상이어야 합니다.";
                if (r.MaxBalloons   is < 1) return $"{at}: maxBalloons는 1 이상이어야 합니다.";
                if (r.ExactBalloons is < 1) return $"{at}: exactBalloons는 1 이상이어야 합니다.";
                if (r.PerBalloons   is < 1) return $"{at}: perBalloons는 1 이상이어야 합니다.";
                if (r.Amount        is < 1) return $"{at}: amount는 1 이상이어야 합니다.";
                if (r.ExactBalloons.HasValue && (r.MinBalloons.HasValue || r.MaxBalloons.HasValue))
                    return $"{at}: exactBalloons는 minBalloons/maxBalloons와 함께 쓸 수 없습니다.";
                if (r.MinBalloons.HasValue && r.MaxBalloons.HasValue && r.MaxBalloons < r.MinBalloons)
                    return $"{at}: maxBalloons는 minBalloons보다 작을 수 없습니다.";
                if (r.PerBalloons.HasValue && r.Amount.HasValue)
                    return $"{at}: perBalloons와 amount는 함께 쓸 수 없습니다.";
            }

            return null;
        }

        // 조건에 맞는 모든 규칙의 수량을 type별로 합산한다.
        private static (int normal, int premium, int monsters) ComputeRewards(RulesFile file, int balloons)
        {
            const int MaxAmount = 1000;
            int normal = 0, premium = 0, monsters = 0;

            foreach (RewardRule r in file.Rules)
            {
                bool matches = r.ExactBalloons.HasValue
                    ? balloons == r.ExactBalloons.Value
                    : balloons >= (r.MinBalloons ?? 1) && balloons <= (r.MaxBalloons ?? int.MaxValue);
                if (!matches) continue;

                int amount = r.PerBalloons.HasValue ? balloons / r.PerBalloons.Value : (r.Amount ?? 1);
                if (amount < 1) continue;

                if (r.Type == KindNormal) normal += amount;
                else if (r.Type == KindPremium) premium += amount;
                else monsters += amount;
            }

            return (Math.Min(normal, MaxAmount), Math.Min(premium, MaxAmount), Math.Min(monsters, MaxAmount));
        }

        private sealed class RulesFile
        {
            public List<RewardRule> Rules { get; set; } = new();

            public static RulesFile CreateDefault() => new()
            {
                Rules = new List<RewardRule>
                {
                    new() { Name = "normal-chest",  Type = KindNormal,  ExactBalloons = 1, Amount = 1 },
                    new() { Name = "premium-chest", Type = KindPremium, ExactBalloons = 2, Amount = 1 },
                    new() { Name = "monster-33",    Type = "monster",   MinBalloons = 33, Amount = 1 }
                }
            };
        }

        private sealed class RewardRule
        {
            public string Name          { get; set; } = "";
            public string Type          { get; set; } = "";
            public int?   MinBalloons   { get; set; }
            public int?   MaxBalloons   { get; set; }
            public int?   ExactBalloons { get; set; }
            public int?   PerBalloons   { get; set; }
            public int?   Amount        { get; set; }
        }

        // ── 상자 아이템 목록 (loot.json) ─────────────────────────────────────
        // 상자 하나를 열면 weight(확률 가중치)에 따라 항목 1개가 나온다. 파일을 고치면 2초 안에 반영된다.

        private void EnsureLootFile()
        {
            if (File.Exists(Path.Combine(Helper.DirectoryPath, LootFileName))) return;
            Helper.Data.WriteJsonFile(LootFileName, LootFile.CreateDefault());
            Monitor.Log("loot.json이 없어 기본 아이템 목록으로 만들었습니다.", LogLevel.Info);
        }

        private void ReloadLoot(bool force)
        {
            DateTime stamp = StampOf(LootFileName);
            if (stamp == DateTime.MinValue) return;
            if (!force && stamp == lootWriteUtc) return;
            lootWriteUtc = stamp;

            try
            {
                LootFile? loaded = Helper.Data.ReadJsonFile<LootFile>(LootFileName);
                string? error = loaded == null ? "파일이 비어 있습니다." : ValidateLoot(loaded);
                if (error != null)
                {
                    Monitor.Log($"loot.json 오류, 이전 목록을 유지합니다: {error}", LogLevel.Error);
                    return;
                }

                loot = loaded!;
                lootNeedsCheck = true;   // 아이템 ID 확인은 게임 스레드에서
                Monitor.Log($"상자 아이템 목록을 반영했습니다: 일반 {loaded!.Chest.Count}개, 고급 {loaded.PremiumChest.Count}개", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Monitor.Log($"loot.json을 읽지 못해 이전 목록을 유지합니다: {ex.Message}", LogLevel.Error);
            }
        }

        private static string? ValidateLoot(LootFile file)
        {
            foreach (var (name, list) in new[] { ("chest", file.Chest), ("premiumChest", file.PremiumChest) })
            {
                if (list == null || list.Count == 0) return $"{name} 목록이 비어 있습니다.";
                for (int i = 0; i < list.Count; i++)
                {
                    LootEntry e = list[i];
                    string at = $"{name}[{i}]";
                    if (!(e.Weight > 0)) return $"{at}: weight는 0보다 커야 합니다.";
                    bool isGold = e.Gold > 0, isItem = !string.IsNullOrWhiteSpace(e.ItemId);
                    if (isGold == isItem) return $"{at}: itemId와 gold 중 하나만 적어야 합니다.";
                    if (isItem && (e.Amount < 1 || e.Amount > 999)) return $"{at}: amount는 1~999여야 합니다.";
                    if (e.Gold < 0) return $"{at}: gold는 0 이상이어야 합니다.";
                }
            }
            return null;
        }

        // 게임에 없는 아이템 ID를 로그로 알려 준다 (뽑기에서는 자동으로 빠진다).
        private void CheckLootItems(LootFile file)
        {
            foreach (var (name, list) in new[] { ("일반", file.Chest), ("고급", file.PremiumChest) })
            {
                double total = list.Sum(e => e.Weight);
                var bad = list.Where(e => e.Gold <= 0 && !ItemRegistry.Exists(e.ItemId)).ToList();
                foreach (var e in bad)
                    Monitor.Log($"loot.json {name} 상자: 게임에 없는 아이템 ID '{e.ItemId}'는 뽑기에서 뺍니다.", LogLevel.Warn);
                if (bad.Count == 0)
                    Monitor.Log($"loot.json {name} 상자 아이템 {list.Count}개 확인 완료 (가중치 합 {total:0.##})", LogLevel.Info);
            }
        }

        private LootEntry? RollLoot(string kind)
        {
            LootFile file = loot;
            var candidates = (kind == KindPremium ? file.PremiumChest : file.Chest)
                .Where(e => e.Weight > 0 && (e.Gold > 0 || ItemRegistry.Exists(e.ItemId)))
                .ToList();
            double total = candidates.Sum(e => e.Weight);
            if (candidates.Count == 0 || total <= 0) return null;

            double pick = random.NextDouble() * total;
            foreach (LootEntry e in candidates)
            {
                pick -= e.Weight;
                if (pick < 0) return e;
            }
            return candidates[^1];
        }

        private sealed class LootEntry
        {
            public string  Name   { get; set; } = "";   // 사람이 읽기 위한 이름 (게임 표시에는 쓰지 않음)
            public string? ItemId { get; set; }
            public int     Amount { get; set; } = 1;
            public int     Gold   { get; set; }
            public double  Weight { get; set; }
        }

        private sealed class LootFile
        {
            public List<LootEntry> Chest        { get; set; } = new();
            public List<LootEntry> PremiumChest { get; set; } = new();

            private static LootEntry I(string name, string id, int amount, double weight) => new() { Name = name, ItemId = id, Amount = amount, Weight = weight };
            private static LootEntry G(int gold, double weight) => new() { Name = $"{gold}골드", Gold = gold, Weight = weight };

            public static LootFile CreateDefault() => new()
            {
                Chest = new List<LootEntry>
                {
                    I("나무", "(O)388", 15, 20), I("돌", "(O)390", 15, 20), I("섬유", "(O)771", 10, 10),
                    I("석탄", "(O)382", 3, 10), I("구리 광석", "(O)378", 5, 10), I("철 광석", "(O)380", 3, 7),
                    I("수액", "(O)92", 10, 6), I("커피", "(O)395", 1, 5), I("기본 비료", "(O)368", 5, 5),
                    I("정동석", "(O)535", 1, 4), I("금 광석", "(O)384", 2, 2), G(500, 1)
                },
                PremiumChest = new List<LootEntry>
                {
                    I("철 주괴", "(O)335", 3, 18), I("단단한 나무", "(O)709", 5, 15), I("고급 비료", "(O)369", 5, 12),
                    I("스피드-그로", "(O)465", 5, 12), I("얼어붙은 정동석", "(O)536", 2, 10), I("금 주괴", "(O)336", 2, 10),
                    I("마그마 정동석", "(O)537", 2, 8), I("고급 스프링클러", "(O)621", 1, 6), I("오색 정동석", "(O)749", 1, 4),
                    I("이리듐 광석", "(O)386", 3, 3), I("다이아몬드", "(O)72", 1, 1.5), I("프리즘 조각", "(O)74", 1, 0.5)
                }
            };
        }

        // ── 몬스터 목록 (monsters.json) ──────────────────────────────────────
        // 몬스터 보상 1마리마다 weight(확률 가중치)에 따라 종류를 뽑는다. 파일을 고치면 2초 안에 반영된다.
        // type에는 아래 CreateMonster에 있는 이름만 쓸 수 있다 (임의 클래스 생성을 막기 위해 목록을 고정).

        private static readonly string[] MonsterTypes =
        {
            "GreenSlime", "FrostJelly", "RedSlime", "Bat", "FrostBat", "LavaBat", "Bug", "Grub", "Fly",
            "DustSpirit", "RockCrab", "Duggy", "Ghost", "Skeleton", "ShadowBrute", "ShadowShaman", "Mummy", "Serpent"
        };

        private static Monster CreateMonster(string type, Vector2 position) => type switch
        {
            "FrostJelly"   => new GreenSlime(position, 40),   // 광산 40층대 슬라임 (파란색)
            "RedSlime"     => new GreenSlime(position, 80),   // 광산 80층대 슬라임 (빨간색)
            "Bat"          => new Bat(position),
            "FrostBat"     => new Bat(position, 40),
            "LavaBat"      => new Bat(position, 80),
            "Bug"          => new Bug(position, 0),
            "Grub"         => new Grub(position),
            "Fly"          => new Fly(position),
            "DustSpirit"   => new DustSpirit(position),
            "RockCrab"     => new RockCrab(position),
            "Duggy"        => new Duggy(position),
            "Ghost"        => new Ghost(position),
            "Skeleton"     => new Skeleton(position, false),
            "ShadowBrute"  => new ShadowBrute(position),
            "ShadowShaman" => new ShadowShaman(position),
            "Mummy"        => new Mummy(position),
            "Serpent"      => new Serpent(position),
            _              => new GreenSlime(position)
        };

        private void EnsureMonstersFile()
        {
            if (File.Exists(Path.Combine(Helper.DirectoryPath, MonstersFileName))) return;
            Helper.Data.WriteJsonFile(MonstersFileName, MonstersFile.CreateDefault());
            Monitor.Log("monsters.json이 없어 기본 몬스터 목록으로 만들었습니다.", LogLevel.Info);
        }

        private void ReloadMonsters(bool force)
        {
            DateTime stamp = StampOf(MonstersFileName);
            if (stamp == DateTime.MinValue) return;
            if (!force && stamp == monstersWriteUtc) return;
            monstersWriteUtc = stamp;

            try
            {
                MonstersFile? loaded = Helper.Data.ReadJsonFile<MonstersFile>(MonstersFileName);
                string? error = loaded == null ? "파일이 비어 있습니다." : ValidateMonsters(loaded);
                if (error != null)
                {
                    Monitor.Log($"monsters.json 오류, 이전 목록을 유지합니다: {error}", LogLevel.Error);
                    return;
                }

                monsters = loaded!;
                Monitor.Log($"몬스터 목록을 반영했습니다: {loaded!.Monsters.Count}종 (가중치 합 {loaded.Monsters.Sum(m => m.Weight):0.##})", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Monitor.Log($"monsters.json을 읽지 못해 이전 목록을 유지합니다: {ex.Message}", LogLevel.Error);
            }
        }

        private static string? ValidateMonsters(MonstersFile file)
        {
            if (file.Monsters == null || file.Monsters.Count == 0) return "monsters 목록이 비어 있습니다.";
            for (int i = 0; i < file.Monsters.Count; i++)
            {
                MonsterEntry m = file.Monsters[i];
                if (!MonsterTypes.Contains(m.Type)) return $"monsters[{i}]: type '{m.Type}'은 지원하지 않습니다. 사용 가능: {string.Join(", ", MonsterTypes)}";
                if (!(m.Weight > 0)) return $"monsters[{i}]: weight는 0보다 커야 합니다.";
                if (m.Rarity is not ("common" or "rare" or "veryRare")) return $"monsters[{i}]: rarity는 common, rare, veryRare 중 하나여야 합니다.";
            }
            return null;
        }

        // 드묾(rare)/아주 드묾(veryRare) 몬스터가 나오면 모든 플레이어 채팅창에 알린다.
        // 서버가 보내는 일반 채팅이라 플레이어 쪽 모드가 옛 버전이어도 보인다.
        private void AnnounceRareMonster(Farmer farmer, MonsterEntry pick)
        {
            MonstersFile file = monsters;
            string? template = pick.Rarity switch
            {
                "rare"     => file.RareMessage,
                "veryRare" => file.VeryRareMessage,
                _          => null
            };
            if (string.IsNullOrWhiteSpace(template)) return;

            string monsterName = string.IsNullOrWhiteSpace(pick.Name) ? pick.Type : pick.Name;
            string text = template.Replace("{player}", farmer.Name).Replace("{monster}", monsterName);
            try
            {
                Game1.Multiplayer.sendChatMessage(LocalizedContentManager.CurrentLanguageCode, text, Multiplayer.AllPlayers);
                Game1.chatBox?.addInfoMessage(text);   // 호스트가 직접 플레이하는 경우 자기 화면에도 표시
                Monitor.Log($"희귀 몬스터 안내: {text}", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Monitor.Log($"희귀 몬스터 안내 실패: {ex.Message}", LogLevel.Warn);
            }
        }

        private MonsterEntry RollMonster()
        {
            List<MonsterEntry> list = monsters.Monsters;
            double total = list.Sum(m => m.Weight);
            double pick = random.NextDouble() * total;
            foreach (MonsterEntry m in list)
            {
                pick -= m.Weight;
                if (pick < 0) return m;
            }
            return list[^1];
        }

        private sealed class MonsterEntry
        {
            public string Name   { get; set; } = "";   // 사람이 읽기 위한 이름 (로그에 표시)
            public string Type   { get; set; } = "GreenSlime";
            public double Weight { get; set; }
            public string Rarity { get; set; } = "common";   // common | rare(드묾) | veryRare(아주 드묾)
        }

        private sealed class MonstersFile
        {
            // {player} = 소환된 플레이어 이름, {monster} = 몬스터 이름. 비워 두면 안내하지 않는다.
            public string RareMessage     { get; set; } = "[희귀 몬스터 등장] {player} 근처에 {monster}이(가) 나타났습니다!";
            public string VeryRareMessage { get; set; } = "[초희귀 몬스터 등장] {player} 근처에 {monster}이(가) 나타났습니다! 조심하세요!";
            public List<MonsterEntry> Monsters { get; set; } = new();

            private static MonsterEntry M(string name, string type, double weight, string rarity = "common") => new() { Name = name, Type = type, Weight = weight, Rarity = rarity };

            public static MonstersFile CreateDefault() => new()
            {
                Monsters = new List<MonsterEntry>
                {
                    M("초록 슬라임", "GreenSlime", 24), M("파란 슬라임", "FrostJelly", 13), M("박쥐", "Bat", 12),
                    M("벌레", "Bug", 10), M("애벌레", "Grub", 10),
                    M("빨간 슬라임", "RedSlime", 5), M("서리 박쥐", "FrostBat", 5), M("먼지 정령", "DustSpirit", 5),
                    M("동굴 파리", "Fly", 4), M("바위 게", "RockCrab", 4), M("두더지", "Duggy", 4),
                    M("용암 박쥐", "LavaBat", 1, "rare"), M("유령", "Ghost", 0.75, "rare"),
                    M("해골", "Skeleton", 0.75, "rare"), M("그림자 무사", "ShadowBrute", 0.75, "rare"),
                    M("그림자 주술사", "ShadowShaman", 0.25, "veryRare"), M("미라", "Mummy", 0.25, "veryRare"),
                    M("날개 달린 뱀", "Serpent", 0.25, "veryRare")
                }
            };
        }

        // ── 데이터 클래스 ────────────────────────────────────────────────────

        private sealed class BridgeConfig
        {
            public int    Port             { get; set; } = 8765;
            public string AuthToken        { get; set; } = "";
            public string CentralServerUrl { get; set; } = "";
            public string AgentToken       { get; set; } = "";
            public int    PollIntervalSeconds { get; set; } = 8;
            // 플레이어 게임: SOOP 연결용 Edge. 비워 두면 기본 설치 경로에서 찾는다.
            public string EdgePath     { get; set; } = "";
            // true = 화면 없이 실행, false = 작은 창으로 실행 (연결 문제를 눈으로 확인할 때)
            public bool   EdgeHeadless { get; set; } = true;
        }

        private sealed class DonationEvent
        {
            public string EventId  { get; set; } = "";
            public string SoopId   { get; set; } = "";
            public int    Balloons { get; set; }
            public long   FarmerId { get; set; }
        }

        private sealed class CentralPollResponse
        {
            public bool        Ok  { get; set; }
            public CentralJob? Job { get; set; }
        }

        private sealed class CentralJob
        {
            public string  Id         { get; set; } = "";
            public string  SoopId     { get; set; } = "";
            public string? FarmerId   { get; set; }
            public string  RewardType { get; set; } = "";
            public int     Amount     { get; set; }
            public string  Reason     { get; set; } = "";

            public long? GetFarmerIdLong()
                => long.TryParse(FarmerId, out long v) ? v : null;
        }

        public sealed class BridgeState
        {
            public List<string>          ProcessedEventIds    { get; set; } = new();
            public Dictionary<long, int> PendingChests        { get; set; } = new();   // 일반 상자
            public Dictionary<long, int> PendingPremiumChests { get; set; } = new();   // 고급 상자
            public Dictionary<long, int> PendingMonsters      { get; set; } = new();
        }
    }
}
