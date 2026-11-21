
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
    internal sealed class PendingMessage        { public int Chests { get; set; } public int PremiumChests { get; set; } public int Items { get; set; } public string FirstItemId { get; set; } = ""; public string Reason { get; set; } = "query"; public string LinkPageUrl { get; set; } = ""; public List<MailSlot> Slots { get; set; } = new(); }
    internal sealed class ClaimRequestMessage   { public string Kind { get; set; } = "all"; public List<MailSlot> Slots { get; set; } = new(); }   // Slots가 있으면 그 칸들만, 없으면 Kind(chest | premiumChest | item | all)로 받는다
    internal sealed class GrantMessage          { public int Chests { get; set; } public int PremiumChests { get; set; } public List<PendingItem> Items { get; set; } = new(); public int RemainingChests { get; set; } public int RemainingPremiumChests { get; set; } public int RemainingItems { get; set; } public string FirstItemId { get; set; } = ""; public List<MailSlot> Slots { get; set; } = new(); }
    // 우편함 한 칸: 같은 아이템 한 묶음 (Amount는 한 칸에 쌓을 수 있는 최대 개수 이하)
    public sealed class MailSlot                { public string Kind { get; set; } = ""; public string ItemId { get; set; } = ""; public int Quality { get; set; } public int Amount { get; set; } public string Name { get; set; } = ""; }
    // 시세 변동: 호스트가 정하고 모든 플레이어에게 알린다. 아이템별 퍼센트는 Seed와 아이템 ID로 같은 값이 나온다.
    public sealed class PriceShiftState         { public int Seed { get; set; } public int ExpiresDay { get; set; } public int MaxIncreasePercent { get; set; } public int MaxDecreasePercent { get; set; } }
    internal sealed class StateSyncMessage      { public PriceShiftState? PriceShift { get; set; } public int FoodHealth { get; set; } public int FoodEnergy { get; set; } public bool Announce { get; set; } }
    internal sealed class ReturnMessage         { public int Chests { get; set; } public int PremiumChests { get; set; } public List<PendingItem> Items { get; set; } = new(); }
    // 관리자 페이지에서 지급한 아이템 (Central 작업 rewardType = "item")
    public sealed class PendingItem             { public string ItemId { get; set; } = ""; public int Amount { get; set; } public int Quality { get; set; } public string Name { get; set; } = ""; }
    internal sealed class OpenRequestMessage    { public string Kind { get; set; } = "chest"; }
    internal sealed class OpenResultMessage     { public string Kind { get; set; } = "chest"; public string ItemId { get; set; } = ""; public int Amount { get; set; } public int Gold { get; set; } public string Error { get; set; } = ""; }
    internal sealed class LinkRequestMessage    { }
    internal sealed class LinkResponseMessage   { public string LoginUrl { get; set; } = ""; public string PageUrl { get; set; } = ""; public string SessionKey { get; set; } = ""; public string Error { get; set; } = ""; }
    internal sealed class UnlinkRequestMessage  { }
    internal sealed class FestivalVoteMessage   { }
    // 호스트 → 플레이어 안내. 서버 채팅으로 보내면 서버 언어(영어) 글꼴로 그려져 한글이 깨지므로, 플레이어 게임이 직접 노란 안내로 띄운다.
    internal sealed class NoticeMessage         { public string Text { get; set; } = ""; }   // 플레이어 → 호스트 (/축제시작)
    internal sealed class UnlinkResponseMessage { public bool Ok { get; set; } public string Error { get; set; } = ""; }
    internal sealed class LinkStatusMessage     { public string Kind { get; set; } = "info"; public string Message { get; set; } = ""; }   // info | success | error
    internal sealed class SavedLink             { public string PageUrl { get; set; } = ""; public string SessionKey { get; set; } = ""; public string SavedAt { get; set; } = ""; }

    // ── 보상 수령 UI: 9×6(54칸) 우편함 (플레이어 게임에서 표시) ───────────────
    // 칸 하나 = 같은 아이템 한 묶음(한 칸에 쌓을 수 있는 최대 개수까지). 칸을 클릭하면 그 칸의 수량 전체를 받는다.
    internal sealed class RewardClaimMenu : IClickableMenu
    {
        private const int Cols = 9, Rows = 6, PerPage = Cols * Rows;
        private const int Cell = 60, Gap = 4, Pad = 36, GridTop = 100, BottomBar = 84;
        private const int GridW = Cols * (Cell + Gap) - Gap;
        private const int GridH = Rows * (Cell + Gap) - Gap;
        private const int MenuWidth  = GridW + Pad * 2;
        private const int MenuHeight = GridTop + GridH + BottomBar;

        private readonly ModEntry _mod;
        private readonly List<Item?> _icons = new();
        private int _iconsVersion = -1;
        private int _page;
        private Rectangle _prevBtn, _nextBtn, _allBtn;
        private ClickableTextureComponent _closeBtn = null!;

        public RewardClaimMenu(ModEntry mod) : base(
            (Game1.uiViewport.Width  - MenuWidth)  / 2,
            (Game1.uiViewport.Height - MenuHeight) / 2,
            MenuWidth, MenuHeight)
        {
            _mod = mod;
            RebuildButtons();
            Game1.playSound("shwip");
        }

        private int PageCount => Math.Max(1, (_mod.ClientSlots.Count + PerPage - 1) / PerPage);

        private Rectangle CellRect(int cellIndex)
        {
            int c = cellIndex % Cols, r = cellIndex / Cols;
            return new Rectangle(xPositionOnScreen + Pad + c * (Cell + Gap), yPositionOnScreen + GridTop + r * (Cell + Gap), Cell, Cell);
        }

        private int CellAt(int x, int y)
        {
            for (int i = 0; i < PerPage; i++) if (CellRect(i).Contains(x, y)) return i;
            return -1;
        }

        // 호스트가 새 목록을 보내면 아이콘을 다시 만든다.
        private void SyncIcons()
        {
            if (_iconsVersion == _mod.ClientSlotsVersion) return;
            _iconsVersion = _mod.ClientSlotsVersion;
            _icons.Clear();
            foreach (MailSlot slot in _mod.ClientSlots) _icons.Add(ModEntry.CreateSlotItem(slot));
            _page = Math.Clamp(_page, 0, PageCount - 1);
        }

        private void RebuildButtons()
        {
            int by = yPositionOnScreen + GridTop + GridH + 18;
            // 이전 | 다음 | (쪽 번호, 가운데) | 전체 수령. 쪽 번호가 버튼에 겹치지 않게 버튼 폭을 줄이고 사이 공간에 둔다.
            _prevBtn = new Rectangle(xPositionOnScreen + Pad, by, 96, 56);
            _nextBtn = new Rectangle(_prevBtn.Right + 8, by, 96, 56);
            _allBtn  = new Rectangle(xPositionOnScreen + width - Pad - 200, by, 200, 56);
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

        private List<MailSlot> PageSlots()
            => _mod.ClientSlots.Skip(_page * PerPage).Take(PerPage).ToList();

        public override void draw(SpriteBatch b)
        {
            SyncIcons();
            b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.4f);
            drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                xPositionOnScreen, yPositionOnScreen, width, height, Color.White, 1f, false);

            DrawCentered(b, "SOOP 우편함", yPositionOnScreen + 24, Game1.textColor);
            int total = _mod.ClientPendingChests + _mod.ClientPendingPremiumChests + _mod.ClientPendingItems;
            string sub = total > 0 ? $"대기 중인 보상 {total}개 · 칸을 클릭하면 받습니다" : "칸을 클릭하면 받습니다";
            Vector2 subSize = Game1.smallFont.MeasureString(sub);
            b.DrawString(Game1.smallFont, sub, new Vector2(xPositionOnScreen + (width - subSize.X) / 2f, yPositionOnScreen + 62), Game1.textColor * 0.7f);
            b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen + 24, yPositionOnScreen + 90, width - 48, 2), Color.Gray * 0.5f);

            bool waiting = _mod.ClientWaiting;
            bool unresponsive = waiting && (DateTime.UtcNow - _mod.ClientWaitingSince).TotalSeconds > 6;
            var slots = _mod.ClientSlots;
            int mx = Game1.getMouseX(), my = Game1.getMouseY();
            Item? hover = null;

            for (int i = 0; i < PerPage; i++)
            {
                Rectangle r = CellRect(i);
                b.Draw(Game1.menuTexture, r, Game1.getSourceRectForStandardTileSheet(Game1.menuTexture, 10), Color.White);
                int idx = _page * PerPage + i;
                if (idx >= slots.Count) continue;

                bool over = r.Contains(mx, my);
                if (over && !waiting) b.Draw(Game1.staminaRect, r, Color.Wheat * 0.4f);
                Item? icon = idx < _icons.Count ? _icons[idx] : null;
                if (icon != null)
                {
                    icon.drawInMenu(b, new Vector2(r.X, r.Y), Cell / 64f);
                    if (over) hover = icon;
                }
                else
                {
                    b.DrawString(Game1.smallFont, "?", new Vector2(r.X + 22, r.Y + 16), Game1.textColor * 0.6f);
                }
            }

            float midY = yPositionOnScreen + GridTop + GridH / 2f - 20;
            if (unresponsive)
            {
                DrawCentered(b, "서버가 응답하지 않습니다.", midY - 20, Game1.textColor * 0.8f);
                DrawCentered(b, "서버에 모드가 설치되어 있는지 확인하세요.", midY + 24, Game1.textColor * 0.7f);
            }
            else if (slots.Count == 0)
            {
                DrawCentered(b, waiting ? "불러오는 중..." : "받을 보상이 없습니다.", midY, Game1.textColor * 0.7f);
            }
            else if (total > slots.Sum(s => s.Amount))
            {
                string more = "표시되지 않은 보상은 받고 나면 이어서 나타납니다.";
                Vector2 ms = Game1.smallFont.MeasureString(more);
                b.DrawString(Game1.smallFont, more, new Vector2(xPositionOnScreen + (width - ms.X) / 2f, yPositionOnScreen + GridTop + GridH + 2), Game1.textColor * 0.6f);
            }

            DrawButton(b, _prevBtn, "이전", _page > 0);
            DrawButton(b, _nextBtn, "다음", _page < PageCount - 1);
            string pageText = $"{_page + 1} / {PageCount}";
            Vector2 ps = Game1.dialogueFont.MeasureString(pageText);
            float gapCenter = (_nextBtn.Right + _allBtn.Left) / 2f;
            b.DrawString(Game1.dialogueFont, pageText, new Vector2(gapCenter - ps.X / 2f, _nextBtn.Y + (_nextBtn.Height - ps.Y) / 2f + 4), Game1.textColor);
            DrawButton(b, _allBtn, "전체 수령", !waiting && PageSlots().Count > 0);

            _closeBtn.draw(b);
            if (hover != null) drawToolTip(b, hover.getDescription(), hover.DisplayName, hover);
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

            if (_prevBtn.Contains(x, y))
            {
                if (_page > 0) { _page--; Game1.playSound("shwip"); }
                return;
            }
            if (_nextBtn.Contains(x, y))
            {
                if (_page < PageCount - 1) { _page++; Game1.playSound("shwip"); }
                return;
            }
            if (_allBtn.Contains(x, y))
            {
                var page = PageSlots();
                if (page.Count > 0) _mod.RequestClaimSlots(page);
                return;
            }

            int cell = CellAt(x, y);
            if (cell < 0) return;
            int idx = _page * PerPage + cell;
            if (idx < _mod.ClientSlots.Count) _mod.RequestClaimSlots(new List<MailSlot> { _mod.ClientSlots[idx] });
        }

        public override void receiveScrollWheelAction(int direction)
        {
            if (direction > 0 && _page > 0) { _page--; Game1.playSound("shwip"); }
            else if (direction < 0 && _page < PageCount - 1) { _page++; Game1.playSound("shwip"); }
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
    internal sealed partial class ModEntry : Mod
    {
        // 일반 상자는 예전 ID를 그대로 써서 이미 받은 상자도 일반 상자가 된다.
        public const string NormalChestId  = "SoopStardewBridge_RewardChest";
        public const string PremiumChestId = "SoopStardewBridge_PremiumChest";
        private const string NormalTextureAsset  = "SoopStardewBridge/reward_chest";
        private const string PremiumTextureAsset = "SoopStardewBridge/premium_chest";
        private const int    MaxGrantPerClaim = 999;   // 한 칸에 쌓을 수 있는 최대 개수

        public const string KindNormal  = "chest";
        public const string KindPremium = "premiumChest";
        public const string KindItem    = "item";      // 관리자 페이지에서 지급한 아이템
        public const string KindAll     = "all";
        public const string KindTax        = "tax";          // 세금 징수 (별풍선 보상)
        public const string KindFood       = "food";         // 뼈고기 지급
        public const string KindPriceShift = "priceShift";   // 시세 변동
        public const string BoneMeatId = "SoopStardewBridge_BoneMeat";
        private const string BoneMeatTextureAsset = "SoopStardewBridge/bone_meat";
        private const int MaxSlotsSent = 270;   // 우편함 한 번에 보내는 최대 칸 수 (5페이지)

        private const string MsgPendingRequest = "PendingRequest";  // 플레이어 → 호스트
        private const string MsgPending        = "Pending";         // 호스트 → 플레이어
        private const string MsgStateSync      = "StateSync";       // 호스트 → 플레이어 (시세 변동, 음식 수치)
        private const string MsgClaimRequest   = "ClaimRequest";    // 플레이어 → 호스트
        private const string MsgGrant          = "Grant";           // 호스트 → 플레이어
        private const string MsgReturn         = "Return";          // 플레이어 → 호스트 (인벤토리에 못 넣은 개수)
        private const string MsgOpenRequest    = "OpenRequest";     // 플레이어 → 호스트 (상자 열기)
        private const string MsgOpenResult     = "OpenResult";      // 호스트 → 플레이어 (뽑힌 아이템)
        private const string MsgLinkRequest    = "LinkRequest";     // 플레이어 → 호스트 (/숲로그인)
        private const string MsgLinkResponse   = "LinkResponse";    // 호스트 → 플레이어 (로그인 주소, 세션 키)
        private const string MsgFestivalVote   = "FestivalVote";    // 플레이어 → 호스트 (/축제시작 찬성)
        private const string MsgUnlinkRequest  = "UnlinkRequest";   // 플레이어 → 호스트 (/숲로그아웃)
        private const string MsgUnlinkResponse = "UnlinkResponse";  // 호스트 → 플레이어
        private const string MsgNotice         = "Notice";          // 호스트 → 플레이어 (노란 안내 문구)
        private const string MsgLinkStatus     = "LinkStatus";      // 호스트 → 플레이어 (SOOP 연동 성공/실패 알림)

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
        private readonly Dictionary<long, List<PendingItem>> lastItemGrant = new();

        // 플레이어 게임 쪽 표시용 값 (호스트가 알려준 값)
        public int      ClientPendingChests;
        public int      ClientPendingPremiumChests;
        public int      ClientPendingItems;          // 지급 아이템 총 개수
        public string   ClientFirstItemId = "";
        public List<MailSlot> ClientSlots = new();   // 우편함 칸 목록 (호스트가 보내 준 값)
        public int      ClientSlotsVersion;
        // 시세 변동 / 음식 (호스트가 정한 값을 모든 플레이어가 같이 쓴다)
        private PriceShiftState? activeShift;
        private int  foodHealth = 50, foodEnergy = 100;
        private bool boneEating;
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IClickableMenu, object> adjustedShops = new();
        public bool     ClientWaiting;
        public DateTime ClientWaitingSince;
        private bool     clientOpening;
        private DateTime clientOpeningSince;
        private bool     clientLinkPending;
        private DateTime? clientLinkWaitingSince;   // /숲로그인 후 결과를 기다리는 중

        // 호스트: Central 응답을 게임 스레드에서 플레이어에게 보내기 위한 대기열
        private readonly ConcurrentQueue<Action> hostMainThreadActions = new();
        // 호스트: Central 폴링 때 함께 보내는 "지금 접속 중인 플레이어" 목록
        private volatile string[] onlineFarmerSnapshot = Array.Empty<string>();
        // 호스트: Central이 알려 준 현재 인증 도메인 (config/central.json). 플레이어가 접속할 때 연결 페이지 주소로 알려 준다.
        private volatile string centralPublicBaseUrl = "";

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
            // Helper.ReadConfig는 읽을 때 파일을 다시 써서 config.json의 // 주석을 지운다. 그래서 쓰기 없이 읽고, 파일이 없을 때만 만든다.
            BridgeConfig? fileConfig = null;
            try { fileConfig = Helper.Data.ReadJsonFile<BridgeConfig>(ConfigFileName); }
            catch (Exception ex) { Monitor.Log($"config.json을 읽지 못해 기본값으로 시작합니다: {ex.Message}", LogLevel.Error); }
            config = fileConfig ?? new BridgeConfig();
            if (fileConfig == null && !File.Exists(Path.Combine(Helper.DirectoryPath, ConfigFileName))) Helper.WriteConfig(config);

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

            // 한 계정 = 캐릭터 하나 (게임 서버에서만 효과가 있다. config.json의 OneCharacterPerAccount로 끄고 켬)
            OneCharacterGate.Apply(ModManifest.UniqueID, Monitor, () => config.OneCharacterPerAccount);
            // 끊긴 연결이 남아 있어도 같은 계정이 다시 들어오면 정리해서 자기 캐릭터로 접속하게 한다 (ReconnectFix.cs)
            ReconnectFix.Apply(ModManifest.UniqueID, Monitor);
            // 소리 없는 게임 서버에서 동굴 파리 등이 매 프레임 오류를 내며 게임을 멈추는 문제 방지
            HeadlessSoundFix.Apply(ModManifest.UniqueID, Monitor);
            InitGoal();   // 공동 목표 (GoalFeature.cs)
            // 축제 이벤트 투표 시작 (JunimoServer가 있는 게임 서버에서만). 모든 모드가 로드된 뒤에 건다.
            Helper.Events.GameLoop.GameLaunched += (_, _) => FestivalVote.Apply(ModManifest.UniqueID, Monitor, () => config.FestivalVote, () => config.FestivalVoteRatio, Notice);

            Helper.Events.GameLoop.SaveLoaded             += OnSaveLoaded;
            Helper.Events.GameLoop.DayStarted             += OnDayStarted;
            Helper.Events.GameLoop.UpdateTicked           += OnUpdateTicked;
            Helper.Events.Input.ButtonPressed             += OnButtonPressed;
            Helper.Events.Content.AssetRequested          += OnAssetRequested;
            Helper.Events.Display.MenuChanged             += OnMenuChanged;
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
                ChatCommands.Register(
                    ModManifest.UniqueID + "_festival",
                    (command, chat) => SendToHost(MsgFestivalVote, new FestivalVoteMessage()),
                    name => "축제 이벤트(달걀 찾기 등) 시작에 찬성합니다. 참가자 과반수가 찬성하면 시작합니다.",
                    aliases: new[] { "축제시작", "festival-start" });
                Monitor.Log("채팅 명령어 등록: /우편함, /mailbox, /숲로그인, /숲로그아웃, /축제시작", LogLevel.Info);
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
                ClientSlots = new(); ClientSlotsVersion++;
                activeShift = null;
                ClientWaiting = false;
                clientOpening = false;
                // 호스트의 답(Pending, reason=login)을 받으면 저장된 SOOP 연결을 다시 띄운다 (현재 인증 도메인을 그때 받는다).
                SendToHost(MsgPendingRequest, new PendingRequestMessage { Reason = "login" });
                return;
            }

            State = Helper.Data.ReadJsonFile<BridgeState>("state.json") ?? State;
            activeShift = State.PriceShift;
            foodHealth = config.Food.BoneMeatHealth; foodEnergy = config.Food.BoneMeatEnergy;
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
            // 시세 변동 기간이 끝나면 원래 가격으로 돌아간다 (모든 플레이어가 같은 날짜 기준으로 스스로 판단한다).
            if (activeShift != null && Game1.Date.TotalDays >= activeShift.ExpiresDay)
            {
                activeShift = null;
                if (Context.IsMainPlayer) { State.PriceShift = null; SaveState(); }
                else Game1.addHUDMessage(new HUDMessage("시세 변동이 끝나 상점 가격이 원래대로 돌아왔습니다.", HUDMessage.newQuest_type));
            }
            if (Context.IsMainPlayer) ProcessPendingRewards();
        }

        // 서버(JunimoServer)는 참가자가 메인 집에 들어가면 "Can't enter main building, porting to your own cabin"
        // 귓속말을 보내고 자기 오두막으로 보낸다. 이동은 그대로 두고, 이 안내 문구만 채팅창에 그려지기 전에 지운다.
        private static void HideMainBuildingNotice()
        {
            List<ChatMessage>? list = Game1.chatBox?.messages;
            if (list == null || list.Count == 0) return;
            for (int i = list.Count - 1; i >= Math.Max(0, list.Count - 5); i--)
            {
                string text = string.Concat(list[i].message.Select(s => s.message ?? ""));
                string compact = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
                if (compact.Contains("Can'tentermainbuilding", StringComparison.OrdinalIgnoreCase))
                    list.RemoveAt(i);
            }
        }

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsMainPlayer) HideMainBuildingNotice();

            // 아이템 목록 확인은 게임 데이터가 준비된 뒤 게임 스레드에서 한다.
            if (lootNeedsCheck && Context.IsGameLaunched && Context.IsMainPlayer)
            {
                lootNeedsCheck = false;
                CheckLootItems(loot);
            }

            if (Context.IsWorldReady) TrackBoneMeatEating();

            // 상자를 열었는데 서버가 답하지 않으면 다시 열 수 있게 풀어 준다.
            if (clientOpening && (DateTime.UtcNow - clientOpeningSince).TotalSeconds > 8)
            {
                clientOpening = false;
                Game1.addHUDMessage(new HUDMessage("서버가 응답하지 않아 상자를 열지 못했습니다.", HUDMessage.error_type));
            }

            // /숲로그인 후 3분 동안 결과가 없으면 안내한다.
            if (clientLinkWaitingSince is DateTime since && (DateTime.UtcNow - since).TotalMinutes > 3)
            {
                clientLinkWaitingSince = null;
                ChatError("아직 SOOP 연동이 완료되지 않았습니다. 브라우저에서 SOOP 로그인을 마쳤는지 확인하고, 안 되면 /숲로그인을 다시 해 주세요.");
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
                ApplyClockSpeed();
            }

            while (incomingEvents.TryDequeue(out DonationEvent? d))
                if (d != null) ProcessDonation(d);

            while (incomingCentralJobs.TryDequeue(out CentralJob? j))
            {
                if (j != null)
                {
                    bool ok = ProcessCentralJob(j, out string error);
                    if (!string.IsNullOrWhiteSpace(j.Id))
                        _ = AcknowledgeCentralJobAsync(j.Id, ok, error);
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

        // 우편함 칸 하나(또는 한 페이지)를 받겠다고 호스트에 요청한다.
        public void RequestClaimSlots(IReadOnlyList<MailSlot> slots)
        {
            if (ClientWaiting || slots.Count == 0) return;

            // 인벤토리에 들어가는 칸만 요청한다. 못 넣는 칸은 호스트 대기열에 그대로 남는다.
            var fits = new List<MailSlot>();
            foreach (MailSlot s in slots)
            {
                Item? item = CreateSlotItem(s);
                if (item != null && Game1.player.couldInventoryAcceptThisItem(item)) fits.Add(s);
            }
            if (fits.Count == 0)
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage(
                    slots.Count == 1 ? "인벤토리가 가득 차 수령할 수 없습니다. 한 칸 이상 비운 뒤 다시 시도하세요."
                                     : "인벤토리에 공간이 없어 수령할 수 없습니다. 한 칸 이상 비운 뒤 다시 시도하세요.",
                    HUDMessage.error_type));
                return;
            }

            ClientWaiting = true;
            ClientWaitingSince = DateTime.UtcNow;
            SendToHost(MsgClaimRequest, new ClaimRequestMessage { Kind = KindAll, Slots = fits });
        }

        // 우편함 칸에 그릴 아이템 (칸의 수량만큼 쌓인 상태)
        public static Item? CreateSlotItem(MailSlot slot)
        {
            try
            {
                if (slot.Kind == KindItem) return CreateGrantItem(slot.ItemId, Math.Max(1, slot.Amount), slot.Quality);
                Item chest = ItemRegistry.Create(ChestIdOf(slot.Kind), 1);
                chest.Stack = Math.Max(1, Math.Min(slot.Amount, chest.maximumStackSize()));
                return chest;
            }
            catch { return null; }
        }
        private void OnClientPending(PendingMessage msg)
        {
            ClientPendingChests = Math.Max(0, msg.Chests);
            ClientPendingPremiumChests = Math.Max(0, msg.PremiumChests);
            ClientPendingItems = Math.Max(0, msg.Items);
            ClientSlots = msg.Slots ?? new List<MailSlot>(); ClientSlotsVersion++;
            ClientFirstItemId = msg.FirstItemId ?? "";
            ClientWaiting = false;

            // 접속 직후 한 번: 전에 /숲로그인 했으면 숨은 Edge를 다시 띄운다.
            if (msg.Reason == "login") ResumeSavedLink(msg.LinkPageUrl);

            // 접속 직후나 새 보상이 들어왔을 때만 알린다 (창을 열어 조회한 경우는 창에 표시됨).
            if (msg.Reason != "query" && ClientPendingChests + ClientPendingPremiumChests + ClientPendingItems > 0)
            {
                var parts = new List<string>();
                if (ClientPendingChests > 0)        parts.Add($"일반 상자 {ClientPendingChests}개");
                if (ClientPendingPremiumChests > 0) parts.Add($"고급 상자 {ClientPendingPremiumChests}개");
                if (ClientPendingItems > 0)         parts.Add($"지급 아이템 {ClientPendingItems}개");
                Game1.addHUDMessage(new HUDMessage(
                    $"SOOP 보상 대기 중: {string.Join(", ", parts)}. F5 또는 /우편함 으로 수령하세요.",
                    HUDMessage.newQuest_type));
            }
        }

        // 지급 아이템 ID → 게임 아이템. ID는 Player_add 형식 "(O)24", "(T)IridiumAxe"를 쓴다.
        // 가공품은 "(O)가공품ID/재료ID" (예: 고대 과일 젤리 "(O)344/454"). 없는 아이템이면 null.
        private static readonly Dictionary<string, StardewValley.Object.PreserveType> PreserveBases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["348"] = StardewValley.Object.PreserveType.Wine,
            ["344"] = StardewValley.Object.PreserveType.Jelly,
            ["342"] = StardewValley.Object.PreserveType.Pickle,
            ["350"] = StardewValley.Object.PreserveType.Juice,
            ["812"] = StardewValley.Object.PreserveType.Roe,
            ["447"] = StardewValley.Object.PreserveType.AgedRoe,
            ["340"] = StardewValley.Object.PreserveType.Honey,
            ["398"] = StardewValley.Object.PreserveType.DriedFruit,
            ["DriedFruit"]     = StardewValley.Object.PreserveType.DriedFruit,
            ["DriedMushrooms"] = StardewValley.Object.PreserveType.DriedMushroom,
            ["SmokedFish"]     = StardewValley.Object.PreserveType.SmokedFish,
            ["SpecificBait"]   = StardewValley.Object.PreserveType.Bait,
        };

        public static Item? CreateGrantItem(string? qualifiedId, int amount, int quality)
        {
            if (string.IsNullOrWhiteSpace(qualifiedId)) return null;
            try
            {
                Item? item;
                var m = System.Text.RegularExpressions.Regex.Match(qualifiedId, @"^\(O\)([^/]+)/(.+)$");
                if (m.Success)
                {
                    if (!PreserveBases.TryGetValue(m.Groups[1].Value, out var type)) return null;
                    if (ItemRegistry.Create("(O)" + m.Groups[2].Value, 1, 0, allowNull: true) is not StardewValley.Object ingredient) return null;
                    item = ItemRegistry.GetObjectTypeDefinition().CreateFlavoredItem(type, ingredient);
                }
                else
                {
                    if (!ItemRegistry.Exists(qualifiedId)) return null;
                    item = ItemRegistry.Create(qualifiedId, 1, 0, allowNull: true);
                }
                if (item == null) return null;
                if (item is StardewValley.Object obj && quality is 1 or 2 or 4) obj.Quality = quality;
                item.Stack = Math.Max(1, Math.Min(amount, item.maximumStackSize()));
                return item;
            }
            catch { return null; }
        }

        // 지급 아이템 하나를 인벤토리에 넣는다. 도구처럼 겹쳐지지 않는 아이템은 한 개씩 넣는다. 반환: 못 넣은 개수
        private static int GiveItem(PendingItem p, out string displayName)
        {
            displayName = p.Name;
            int remaining = Math.Max(0, p.Amount);
            while (remaining > 0)
            {
                Item? item = CreateGrantItem(p.ItemId, remaining, p.Quality);
                if (item == null) break;
                displayName = item.DisplayName;
                int put = item.Stack;
                Item? left = Game1.player.addItemToInventory(item);
                int notPut = left?.Stack ?? 0;
                remaining -= put - notPut;
                if (notPut > 0) break;
            }
            return remaining;
        }

        private int AddToInventory(string itemId, int amount)
        {
            // 한 칸에 쌓을 수 있는 최대 개수씩 나눠 넣는다. 반환: 못 넣은 개수
            int remaining = Math.Max(0, amount);
            while (remaining > 0)
            {
                Item item = ItemRegistry.Create(itemId, 1);
                int stack = Math.Min(remaining, Math.Max(1, item.maximumStackSize()));
                item.Stack = stack;
                Item? left = Game1.player.addItemToInventory(item);
                int notPut = left?.Stack ?? 0;
                remaining -= stack - notPut;
                if (notPut > 0) break;
            }
            return remaining;
        }

        private void OnClientGrant(GrantMessage msg)
        {
            ClientWaiting = false;
            int gotNormal  = Math.Max(0, msg.Chests);
            int gotPremium = Math.Max(0, msg.PremiumChests);

            int backNormal  = AddToInventory(NormalChestId,  gotNormal);
            int backPremium = AddToInventory(PremiumChestId, gotPremium);

            // 지급 아이템: 넣을 수 있는 만큼 넣고, 남은 것은 호스트 대기열로 돌려보낸다.
            var backItems = new List<PendingItem>();
            var gotItemNames = new List<string>();
            int gotItems = 0;
            foreach (PendingItem p in msg.Items ?? new List<PendingItem>())
            {
                int left = GiveItem(p, out string name);
                int added = p.Amount - left;
                if (added > 0) { gotItems += added; gotItemNames.Add($"{name} {added}개"); }
                if (left > 0) backItems.Add(new PendingItem { ItemId = p.ItemId, Amount = left, Quality = p.Quality, Name = p.Name });
            }

            if (backNormal + backPremium > 0 || backItems.Count > 0)
                SendToHost(MsgReturn, new ReturnMessage { Chests = backNormal, PremiumChests = backPremium, Items = backItems });

            int addedNormal  = gotNormal  - backNormal;
            int addedPremium = gotPremium - backPremium;
            int backItemCount = backItems.Sum(i => i.Amount);
            ClientPendingChests        = Math.Max(0, msg.RemainingChests) + backNormal;
            ClientPendingPremiumChests = Math.Max(0, msg.RemainingPremiumChests) + backPremium;
            ClientPendingItems         = Math.Max(0, msg.RemainingItems) + backItemCount;
            ClientFirstItemId          = backItems.FirstOrDefault()?.ItemId ?? msg.FirstItemId ?? "";
            ClientSlots = msg.Slots ?? new List<MailSlot>(); ClientSlotsVersion++;   // 못 넣어 돌려보낸 몫은 호스트가 곧 새 목록으로 알려 준다

            if (addedNormal + addedPremium + gotItems > 0)
            {
                var parts = new List<string>();
                if (addedNormal  > 0) parts.Add($"일반 상자 {addedNormal}개");
                if (addedPremium > 0) parts.Add($"고급 상자 {addedPremium}개");
                if (gotItemNames.Count > 0) parts.Add(gotItemNames.Count <= 3 ? string.Join(", ", gotItemNames) : $"지급 아이템 {gotItems}개");
                string text = $"{string.Join(", ", parts)}를 받았습니다.";
                int waiting = backNormal + backPremium + backItemCount;
                if (waiting > 0) text += $" 공간이 부족해 {waiting}개는 대기 중입니다.";
                Game1.playSound("coin");
                Game1.addHUDMessage(new HUDMessage(text, HUDMessage.newQuest_type));
            }
            else
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage(
                    gotNormal + gotPremium + (msg.Items?.Count ?? 0) > 0 ? "인벤토리에 공간이 없습니다. 한 칸 이상 비운 뒤 다시 수령하세요." : "수령할 보상이 없습니다.",
                    HUDMessage.error_type));
            }

            if (ClientPendingChests + ClientPendingPremiumChests + ClientPendingItems <= 0 && Game1.activeClickableMenu is RewardClaimMenu menu)
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
            State.PendingItems.TryGetValue(farmerId, out List<PendingItem>? items);
            string baseUrl = centralPublicBaseUrl;
            SendToPlayer(farmerId, MsgPending, new PendingMessage
            {
                Chests = normal, PremiumChests = premium, Reason = reason,
                Items = items?.Sum(i => i.Amount) ?? 0, FirstItemId = items?.FirstOrDefault()?.ItemId ?? "",
                LinkPageUrl = string.IsNullOrEmpty(baseUrl) ? "" : baseUrl + "/link/page",
                Slots = BuildSlots(farmerId)
            });
        }

        private void OnHostPendingRequest(long fromId, PendingRequestMessage msg)
        {
            SendPending(fromId, msg.Reason == "login" ? "login" : "query");
            if (msg.Reason == "login") SendToPlayer(fromId, MsgStateSync, BuildStateSync(false));   // 접속한 플레이어에게 시세 변동·음식 수치를 알려 준다
        }

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
            int normal = 0, premium = 0;
            var items = new List<PendingItem>();
            if (msg.Slots is { Count: > 0 })
            {
                // 우편함에서 고른 칸만 받는다. 칸에 적힌 수량까지만, 실제 대기 중인 만큼만 내준다.
                foreach (MailSlot s in msg.Slots.Take(MaxSlotsSent))
                {
                    int want = Math.Max(1, s.Amount);
                    switch (s.Kind)
                    {
                        case KindNormal:  normal  += Take(State.PendingChests,        fromId, want); break;
                        case KindPremium: premium += Take(State.PendingPremiumChests, fromId, want); break;
                        case KindItem:    TakeItem(fromId, s.ItemId, s.Quality, want, items); break;
                    }
                }
            }
            else
            {
                normal  = msg.Kind is KindNormal  or KindAll ? Take(State.PendingChests,        fromId, int.MaxValue) : 0;
                premium = msg.Kind is KindPremium or KindAll ? Take(State.PendingPremiumChests, fromId, int.MaxValue) : 0;
                if (msg.Kind is KindItem or KindAll && State.PendingItems.Remove(fromId, out List<PendingItem>? taken)) items = taken;
            }

            if (normal + premium > 0 || items.Count > 0)
            {
                lastGrant[fromId] = (normal, premium);
                lastItemGrant[fromId] = items.Select(i => new PendingItem { ItemId = i.ItemId, Amount = i.Amount, Quality = i.Quality, Name = i.Name }).ToList();
                SaveState();
                Monitor.Log($"보상 지급: FarmerId={fromId}, 일반={normal}, 고급={premium}, 아이템={string.Join(", ", items.Select(i => $"{i.ItemId}x{i.Amount}"))}", LogLevel.Info);
            }

            State.PendingChests.TryGetValue(fromId, out int leftNormal);
            State.PendingPremiumChests.TryGetValue(fromId, out int leftPremium);
            State.PendingItems.TryGetValue(fromId, out List<PendingItem>? leftItems);
            SendToPlayer(fromId, MsgGrant, new GrantMessage
            {
                Chests = normal, PremiumChests = premium, Items = items,
                RemainingChests = leftNormal, RemainingPremiumChests = leftPremium,
                RemainingItems = leftItems?.Sum(i => i.Amount) ?? 0, FirstItemId = leftItems?.FirstOrDefault()?.ItemId ?? "",
                Slots = BuildSlots(fromId)
            });
        }

        private void OnHostReturn(long fromId, ReturnMessage msg)
        {
            lastGrant.TryGetValue(fromId, out var granted);
            lastGrant.Remove(fromId);
            // 방금 보낸 양보다 많이 돌려받지 않는다.
            int backNormal  = Math.Min(Math.Max(0, msg.Chests),        granted.Chests);
            int backPremium = Math.Min(Math.Max(0, msg.PremiumChests), granted.Premium);

            // 아이템도 방금 보낸 것(같은 ID·품질)만, 보낸 개수까지만 돌려받는다.
            lastItemGrant.Remove(fromId, out List<PendingItem>? grantedItems);
            var backItems = new List<PendingItem>();
            foreach (PendingItem r in msg.Items ?? new List<PendingItem>())
            {
                PendingItem? g = grantedItems?.FirstOrDefault(x => x.ItemId == r.ItemId && x.Quality == r.Quality && x.Amount > 0);
                if (g == null) continue;
                int n = Math.Min(Math.Max(0, r.Amount), g.Amount);
                if (n <= 0) continue;
                g.Amount -= n;
                backItems.Add(new PendingItem { ItemId = g.ItemId, Amount = n, Quality = g.Quality, Name = g.Name });
            }
            if (backNormal + backPremium <= 0 && backItems.Count == 0) return;

            AddPending(State.PendingChests,        fromId, backNormal);
            AddPending(State.PendingPremiumChests, fromId, backPremium);
            if (backItems.Count > 0)
            {
                if (!State.PendingItems.TryGetValue(fromId, out List<PendingItem>? list)) State.PendingItems[fromId] = list = new List<PendingItem>();
                list.InsertRange(0, backItems);
            }
            SaveState();
            Monitor.Log($"보상 반환(인벤토리 부족): FarmerId={fromId}, 일반={backNormal}, 고급={backPremium}, 아이템={backItems.Sum(i => i.Amount)}개", LogLevel.Info);
            SendPending(fromId, "query");   // 돌려받은 몫까지 포함한 새 우편함 목록을 보낸다
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
                        case MsgFestivalVote:   HandleHostMessage(e.Type, e.FromPlayerID, e.ReadAs<FestivalVoteMessage>());   break;
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
                        case MsgLinkStatus:     HandleClientMessage(e.Type, e.ReadAs<LinkStatusMessage>());     break;
                        case MsgNotice:         HandleClientMessage(e.Type, e.ReadAs<NoticeMessage>());         break;
                        case MsgStateSync:      HandleClientMessage(e.Type, e.ReadAs<StateSyncMessage>());      break;
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
                case FestivalVoteMessage     when type == MsgFestivalVote:   FestivalVote.OnVote(fromId);     break;
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
                case LinkStatusMessage m     when type == MsgLinkStatus:     OnClientLinkStatus(m);     break;
                case NoticeMessage m         when type == MsgNotice:         ChatInfo(m.Text);          break;
                case StateSyncMessage m      when type == MsgStateSync:      OnClientStateSync(m);      break;
            }
        }

        // ── SOOP 계정 연결 (/숲로그인, /숲로그아웃) ──────────────────────────
        // 1) 플레이어 게임이 호스트에 요청 → 2) 호스트가 Central에서 로그인 주소와 세션 키를 받음
        // 3) 플레이어 PC의 기본 브라우저로 SOOP 로그인 → 4) 플레이어 PC에서 숨은 Edge가 Central 연결 페이지를 열어 SDK 연결
        // 숨은 Edge는 게임이 켜져 있는 동안만 동작한다. 게임을 끄거나 타이틀로 나가면 끈다.

        // 호스트: 안내 문구를 플레이어 게임 채팅창에 노란 글씨로 띄운다. target이 null이면 접속 중인 모두에게.
        public void Notice(long? target, string text)
        {
            var msg = new NoticeMessage { Text = text };
            if (target is long one) { SendToPlayer(one, MsgNotice, msg); return; }
            foreach (Farmer f in Game1.getOnlineFarmers()) SendToPlayer(f.UniqueMultiplayerID, MsgNotice, msg);
        }

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
                clientLinkWaitingSince = DateTime.UtcNow;
                ChatInfo("브라우저에서 SOOP 로그인을 마치면 연동 결과가 여기에 표시됩니다.");
            }
            catch (Exception ex)
            {
                Monitor.Log($"기본 브라우저 열기 실패: {ex.Message}", LogLevel.Warn);
                ChatError($"브라우저를 열지 못했습니다. 이 주소를 직접 열어 주세요: {msg.LoginUrl}");
            }
        }

        // Central이 알려 준 연동 결과를 채팅창과 화면 알림으로 표시한다.
        private void OnClientLinkStatus(LinkStatusMessage msg)
        {
            if (string.IsNullOrWhiteSpace(msg.Message)) return;
            switch (msg.Kind)
            {
                case "success":
                    clientLinkWaitingSince = null;
                    ChatInfo(msg.Message);
                    Game1.addHUDMessage(new HUDMessage(msg.Message, HUDMessage.achievement_type));
                    Game1.playSound("reward");
                    break;
                case "loss":   // 손해 알림: 빨간 "에러" 표시 없이 일반 채팅 + 경고 알림으로 보여 준다
                    ChatInfo(msg.Message);
                    Game1.addHUDMessage(new HUDMessage(msg.Message, HUDMessage.error_type));
                    Game1.playSound("cancel");
                    break;
                case "notice":
                    ChatInfo(msg.Message);
                    Game1.addHUDMessage(new HUDMessage(msg.Message, HUDMessage.newQuest_type));
                    Game1.playSound("reward");
                    break;
                case "error":
                    clientLinkWaitingSince = null;
                    ChatError(msg.Message);
                    Game1.addHUDMessage(new HUDMessage(msg.Message, HUDMessage.error_type));
                    Game1.playSound("cancel");
                    break;
                default:
                    ChatInfo(msg.Message);
                    break;
            }
        }

        // 전에 /숲로그인 했으면 접속할 때 숨은 Edge를 다시 띄운다 (SOOP 로그인이 유효하면 바로 연결된다).
        // currentPageUrl: 호스트가 알려 준 현재 인증 도메인의 연결 페이지. 관리자가 도메인을 바꿨으면 저장된 주소 대신 이걸 쓴다.
        private void ResumeSavedLink(string? currentPageUrl)
        {
            if (!OperatingSystem.IsWindows()) return;
            if (edgeProcess != null && !edgeProcess.HasExited) return;
            SavedLink? saved = Helper.Data.ReadJsonFile<SavedLink>(LinkFileFor(Game1.player.UniqueMultiplayerID));
            if (saved == null || string.IsNullOrEmpty(saved.SessionKey)) return;
            if (!string.IsNullOrWhiteSpace(currentPageUrl) && currentPageUrl != saved.PageUrl)
            {
                Monitor.Log($"인증 도메인이 바뀌어 연결 페이지 주소를 갱신합니다: {currentPageUrl}", LogLevel.Info);
                saved.PageUrl = currentPageUrl;
                Helper.Data.WriteJsonFile(LinkFileFor(Game1.player.UniqueMultiplayerID), saved);
            }
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

                // 한꺼번에 많이 소환하면 장소 동기화 데이터가 Steam 한도를 넘어 다른 플레이어 연결이 끊긴다.
                // 그래서 1초에 MonsterSpawnPerSecond마리씩, 장소 안 몬스터가 MaxMonstersPerLocation 미만일 때만 소환하고 나머지는 대기열에 남긴다.
                GameLocation loc = farmer.currentLocation;
                // 병원(쓰러져서 실려 간 곳) 같은 소환 금지 장소, 축제, 쓰러진 상태에서는 소환하지 않고 기다린다.
                if (!CanSpawnMonstersFor(farmer, loc)) continue;
                int alive = loc.characters.Count(c => c is Monster);
                int room  = Math.Max(0, Math.Max(1, config.MaxMonstersPerLocation) - alive);
                int batch = Math.Min(entry.Value, Math.Min(room, Math.Max(1, config.MonsterSpawnPerSecond)));
                if (batch <= 0) continue;   // 장소가 가득 참: 몬스터가 줄면 이어서 소환

                var spawnedNames = new List<string>();
                var announce = new Dictionary<string, (MonsterEntry Pick, int Count)>();
                int spawned = 0;
                for (int i = 0; i < batch; i++)
                {
                    // 건물·울타리·나무·물 위처럼 막힌 칸에는 놓지 않는다 (몬스터가 끼어서 못 움직이는 문제)
                    Vector2? spawn = FindMonsterSpawnTile(loc, farmer.Tile);
                    if (spawn == null) break;   // 빈 칸이 없음: 남은 수는 다음에 소환
                    MonsterEntry pick = RollMonster();
                    loc.characters.Add(CreateMonster(pick.Type, spawn.Value * 64f));
                    string name = string.IsNullOrWhiteSpace(pick.Name) ? pick.Type : pick.Name;
                    spawnedNames.Add(name);
                    announce[name] = announce.TryGetValue(name, out var a) ? (pick, a.Count + 1) : (pick, 1);
                    spawned++;
                }
                if (spawned == 0) continue;
                batch = spawned;
                // 희귀 알림은 한 번에 묶어서 보낸다 (몬스터마다 보내면 채팅창이 도배됨)
                foreach (var a in announce.Values) AnnounceRareMonster(farmer, a.Pick, a.Count);
                int left = entry.Value - batch;
                if (left > 0) State.PendingMonsters[farmerId] = left; else State.PendingMonsters.Remove(farmerId);
                Monitor.Log($"몬스터 소환: FarmerId={farmerId}, {batch}마리 (남은 {left}, 장소 몬스터 {alive + batch}/{config.MaxMonstersPerLocation}), 종류={string.Join(", ", spawnedNames)}", LogLevel.Info);
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
            if (e.NameWithoutLocale.IsEquivalentTo(BoneMeatTextureAsset))
            {
                e.LoadFromModFile<Texture2D>("assets/bone_meat.png", AssetLoadPriority.Exclusive);
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
                    // 뼈고기: 기본 회복량은 0으로 두고, 먹기를 마치면 config의 체력/기력 수치를 직접 적용한다.
                    shownFood = (foodHealth, foodEnergy);
                    data[BoneMeatId] = new ObjectData
                    {
                        Name = BoneMeatId, DisplayName = "뼈고기",
                        Description = $"뼈째 들고 뜯어 먹는 든든한 고기.^체력 +{Math.Max(0, foodHealth)}, 기력 +{Math.Max(0, foodEnergy)}",
                        Type = "Cooking", Category = -7, Price = 0, Edibility = 0,
                        Texture = BoneMeatTextureAsset, SpriteIndex = 0
                    };
                });
            }
        }

        // ── 감사 로그 ────────────────────────────────────────────────────────

        // ── 우편함 칸 계산 (호스트) ──────────────────────────────────────────

        private static int StackLimitOf(string itemId, int quality)
            => CreateGrantItem(itemId, 1, quality)?.maximumStackSize() ?? 999;

        // 대기 중인 보상을 "같은 아이템끼리 묶고, 한 칸 최대 개수에서 잘라" 칸 목록으로 만든다.
        private List<MailSlot> BuildSlots(long farmerId)
        {
            var list = new List<MailSlot>();
            void AddStacks(string kind, string itemId, int quality, int total, string name)
            {
                if (total <= 0) return;
                int max = Math.Max(1, kind == KindItem ? StackLimitOf(itemId, quality) : (ItemRegistry.Create(itemId, 1)?.maximumStackSize() ?? 999));
                while (total > 0 && list.Count < MaxSlotsSent)
                {
                    int n = Math.Min(total, max);
                    list.Add(new MailSlot { Kind = kind, ItemId = itemId, Quality = quality, Amount = n, Name = name });
                    total -= n;
                }
            }

            State.PendingChests.TryGetValue(farmerId, out int normal);
            State.PendingPremiumChests.TryGetValue(farmerId, out int premium);
            AddStacks(KindNormal,  NormalChestId,  0, normal,  "SOOP 일반 상자");
            AddStacks(KindPremium, PremiumChestId, 0, premium, "SOOP 고급 상자");
            if (State.PendingItems.TryGetValue(farmerId, out List<PendingItem>? items))
                foreach (var g in items.Where(i => i.Amount > 0).GroupBy(i => (i.ItemId, i.Quality)))
                    AddStacks(KindItem, g.Key.ItemId, g.Key.Quality, g.Sum(i => i.Amount), g.First().Name);
            return list;
        }

        // 대기 중인 지급 아이템에서 같은 ID·품질의 아이템을 want개까지 꺼내 into에 모은다.
        private void TakeItem(long farmerId, string itemId, int quality, int want, List<PendingItem> into)
        {
            if (!State.PendingItems.TryGetValue(farmerId, out List<PendingItem>? list)) return;
            foreach (PendingItem p in list)
            {
                if (want <= 0) break;
                if (p.Amount <= 0 || p.ItemId != itemId || p.Quality != quality) continue;
                int n = Math.Min(p.Amount, want);
                p.Amount -= n; want -= n;
                PendingItem? ex = into.FirstOrDefault(x => x.ItemId == p.ItemId && x.Quality == p.Quality);
                if (ex != null) ex.Amount += n;
                else into.Add(new PendingItem { ItemId = p.ItemId, Amount = n, Quality = p.Quality, Name = p.Name });
            }
            list.RemoveAll(p => p.Amount <= 0);
            if (list.Count == 0) State.PendingItems.Remove(farmerId);
        }

        private void AddPendingItem(long farmerId, PendingItem add)
        {
            if (!State.PendingItems.TryGetValue(farmerId, out List<PendingItem>? list)) State.PendingItems[farmerId] = list = new List<PendingItem>();
            PendingItem? ex = list.FirstOrDefault(x => x.ItemId == add.ItemId && x.Quality == add.Quality);
            if (ex != null) ex.Amount += add.Amount; else list.Add(add);
        }

        // ── 세금 징수 / 시세 변동 / 음식 ─────────────────────────────────────

        private void BroadcastNotice(string kind, string text)
        {
            var status = new LinkStatusMessage { Kind = kind, Message = text };
            foreach (Farmer f in Game1.getOnlineFarmers()) SendToPlayer(f.UniqueMultiplayerID, MsgLinkStatus, status);
        }

        // 공용 자금에서 config의 Tax.Percent만큼 징수한다. 한 명의 후원으로 서버 전체 자금이 줄어든다.
        private bool ApplyTax(out string error)
        {
            error = "";
            if (Game1.player.team.useSeparateWallets.Value)
            {
                error = "개인 지갑 모드에서는 세금 징수를 지원하지 않습니다. (공용 지갑에서만 동작)";
                return false;
            }
            int min = Math.Clamp(config.Tax.MinPercent, 0, 100), max = Math.Clamp(config.Tax.MaxPercent, 0, 100);
            if (max < min) (min, max) = (max, min);   // 거꾸로 적어도 동작하게
            int pct = random.Next(min, max + 1);
            int before = Game1.player.Money;
            int loss = (int)Math.Floor(before * (pct / 100.0));
            if (loss > 0) Game1.player.Money = before - loss;
            Monitor.Log($"세금 징수: 공용 자금 {before}G 중 {pct}% (설정 {min}~{max}%) = {loss}G", LogLevel.Info);
            BroadcastNotice("loss", loss > 0
                ? $"세금 징수! 공용 자금 {before:N0}G 중 {pct}%인 {loss:N0}G가 징수되었습니다."
                : "세금 징수! 하지만 징수할 돈이 없습니다.");
            return true;
        }

        // 새 시세 변동을 시작한다. 이미 진행 중이던 변동은 사라지고 새것으로 바뀐다.
        private void ApplyPriceShiftJob()
        {
            PriceShiftConfig cfg = config.PriceShift;
            State.PriceShift = new PriceShiftState
            {
                Seed = random.Next(),
                ExpiresDay = Game1.Date.TotalDays + Math.Max(1, cfg.Days),
                MaxIncreasePercent = Math.Clamp(cfg.MaxIncreasePercent, 0, 1000),
                MaxDecreasePercent = Math.Clamp(cfg.MaxDecreasePercent, 0, 90)
            };
            activeShift = State.PriceShift;
            SaveState();
            Monitor.Log($"시세 변동 시작: +{State.PriceShift.MaxIncreasePercent}% / -{State.PriceShift.MaxDecreasePercent}% 범위, {Math.Max(1, cfg.Days)}일간", LogLevel.Info);
            BroadcastState(announce: true);
        }

        private StateSyncMessage BuildStateSync(bool announce) => new()
        {
            PriceShift = State.PriceShift,
            FoodHealth = config.Food.BoneMeatHealth,
            FoodEnergy = config.Food.BoneMeatEnergy,
            Announce = announce
        };

        private void BroadcastState(bool announce)
        {
            StateSyncMessage msg = BuildStateSync(announce);
            foreach (Farmer f in Game1.getOnlineFarmers()) SendToPlayer(f.UniqueMultiplayerID, MsgStateSync, msg);
        }

        // 뼈고기 설명에 적힌 체력/기력 숫자를 config 값과 맞춘다 (값이 바뀌었을 때만 아이템 데이터를 다시 읽는다).
        private (int, int) shownFood = (-1, -1);   // 아이템 데이터를 만들 때 쓴 값
        private void RefreshBoneMeatText()
        {
            if (shownFood == (-1, -1) || shownFood == (foodHealth, foodEnergy)) return;
            Helper.GameContent.InvalidateCache("Data/Objects");
        }

        private void OnClientStateSync(StateSyncMessage msg)
        {
            activeShift = msg.PriceShift;
            foodHealth = Math.Max(0, msg.FoodHealth);
            foodEnergy = Math.Max(0, msg.FoodEnergy);
            RefreshBoneMeatText();
            if (msg.Announce && activeShift != null && Context.IsWorldReady)
            {
                int days = Math.Max(1, activeShift.ExpiresDay - Game1.Date.TotalDays);
                string text = $"시세 변동! 상점 가격이 아이템마다 달라집니다 (최대 +{activeShift.MaxIncreasePercent}% / -{activeShift.MaxDecreasePercent}%, {days}일간).";
                ChatInfo(text);
                Game1.addHUDMessage(new HUDMessage(text, HUDMessage.newQuest_type));
                Game1.playSound("reward");
            }
        }

        // 아이템 ID마다 같은 값이 나오는 "랜덤" 퍼센트: Seed가 같으면 모든 플레이어가 같은 가격을 본다.
        private static int ShiftPercent(PriceShiftState st, string itemKey)
        {
            int up = Math.Max(0, st.MaxIncreasePercent), down = Math.Max(0, st.MaxDecreasePercent);
            uint h = 2166136261u;
            foreach (char ch in itemKey) { h ^= ch; h *= 16777619u; }
            h ^= (uint)st.Seed; h *= 16777619u; h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
            return -down + (int)(h % (uint)(up + down + 1));
        }

        private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
        {
            if (e.NewMenu is ShopMenu shop) ApplyShopPrices(shop);
        }

        // 상점이 열릴 때, 판매 중인 모든 아이템의 가격을 아이템별 퍼센트로 바꾼다. (골드로 사는 상점만)
        private void ApplyShopPrices(ShopMenu shop)
        {
            PriceShiftState? st = activeShift;
            if (st == null || !Context.IsWorldReady || Game1.Date.TotalDays >= st.ExpiresDay) return;
            if (adjustedShops.TryGetValue(shop, out _)) return;
            adjustedShops.Add(shop, new object());
            if (shop.currency != 0) return;

            foreach (ISalable key in shop.itemPriceAndStock.Keys.ToList())
            {
                ItemStockInformation info = shop.itemPriceAndStock[key];
                if (info.TradeItem != null || info.Price <= 0) continue;
                int pct = ShiftPercent(st, key.QualifiedItemId);
                int price = Math.Max(1, (int)Math.Round(info.Price * (100 + pct) / 100.0));
                if (price == info.Price) continue;
                info.Price = price;
                shop.itemPriceAndStock[key] = info;
            }
        }

        // 뼈고기를 먹는 동작이 끝나는 순간 config의 체력/기력만큼 회복한다.
        private void TrackBoneMeatEating()
        {
            Farmer? p = Game1.player;
            if (p == null) return;
            if (p.isEating)
            {
                if (p.itemToEat?.QualifiedItemId == "(O)" + BoneMeatId) boneEating = true;
            }
            else if (boneEating)
            {
                boneEating = false;
                int h = Math.Max(0, foodHealth), s = Math.Max(0, foodEnergy);
                p.health = Math.Min(p.maxHealth, p.health + h);
                p.Stamina = Math.Min(p.MaxStamina, p.Stamina + s);
                Game1.addHUDMessage(new HUDMessage($"뼈고기를 먹었습니다. 체력 +{h}, 기력 +{s}", HUDMessage.newQuest_type));
            }
        }
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
                        if (!string.IsNullOrWhiteSpace(poll?.PublicBaseUrl)) centralPublicBaseUrl = poll!.PublicBaseUrl!.TrimEnd('/');
                        // SOOP 연동 결과를 해당 플레이어 게임에 알린다 (게임 스레드에서 전송).
                        foreach (LinkEventDto ev in poll?.LinkEvents ?? new List<LinkEventDto>())
                        {
                            if (!long.TryParse(ev.FarmerId, out long target)) continue;
                            var status = new LinkStatusMessage { Kind = ev.Kind, Message = ev.Message };
                            hostMainThreadActions.Enqueue(() => { if (FindOnlineFarmer(target) != null) SendToPlayer(target, MsgLinkStatus, status); });
                            Monitor.Log($"SOOP 알림 전달 → FarmerId={target} ({ev.Kind})", LogLevel.Info);   // 문구에 후원자 닉네임이 들어 있어 내용은 남기지 않는다
                        }
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

        private async Task AcknowledgeCentralJobAsync(string jobId, bool processed, string error = "")
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
                    message = processed ? "SMAPI가 보상을 처리했습니다." : (string.IsNullOrEmpty(error) ? "FarmerId가 없거나 플레이어가 오프라인 상태입니다." : error)
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
            // 예전 로컬 브리지가 후원을 직접 보내는 수신 포트. Windows에서는 관리자 권한 없이 열 수 있는 localhost로만 연다
            // (스트리머 PC에는 필요 없음). 게임 서버(Docker, Linux)는 컨테이너 밖에서 들어오도록 모든 주소로 연다.
            listener.Prefixes.Add(OperatingSystem.IsWindows() ? $"http://localhost:{config.Port}/" : $"http://+:{config.Port}/");
            try
            {
                listener.Start();
                _ = ListenLoopAsync(listener, listenerCancellation.Token);
            }
            catch (Exception ex)
            { Monitor.Log($"예전 연동용 수신 포트({config.Port})를 열지 못했습니다. 지금 연동 방식(/숲로그인)에는 영향이 없습니다: {ex.Message}", LogLevel.Trace); listener.Close(); listener = null; }
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
            { Monitor.Log($"잘못된 후원 수량: {eventId}", LogLevel.Warn); return; }

            long? targetFarmerId = donation.FarmerId != 0 ? donation.FarmerId : null;   // 플레이어 ID는 음수일 수 있다
            if (targetFarmerId == null)
            { Monitor.Log($"FarmerId 없음 (event={eventId})", LogLevel.Warn); RememberEvent(eventId); return; }

            int balloons = donation.Balloons;
            var (normal, premium, monsters) = ComputeRewards(rules, balloons);   // rules.json 기준

            if (normal + premium + monsters == 0)
            { RememberEvent(eventId); Monitor.Log($"보상 기준 미달: {balloons}개", LogLevel.Info); return; }

            AddPending(State.PendingChests,        targetFarmerId.Value, normal);
            AddPending(State.PendingPremiumChests, targetFarmerId.Value, premium);
            AddPending(State.PendingMonsters,      targetFarmerId.Value, monsters);
            RememberEvent(eventId);

            Monitor.Log($"후원 이벤트 접수: {eventId}, {balloons}개 → 일반:{normal} 고급:{premium} 몬스터:{monsters}", LogLevel.Info);
            if (normal + premium > 0) NotifyPending(targetFarmerId.Value);
            ProcessPendingRewards();
        }

        private bool ProcessCentralJob(CentralJob job, out string error)
        {
            error = "";
            string jobKey = "central:" + job.Id.Trim();
            if (State.ProcessedEventIds.Contains(jobKey))
            { Monitor.Log($"Central 중복 무시: {job.Id}", LogLevel.Warn); return true; }

            long? fid = job.GetFarmerIdLong();
            if (fid == null || fid == 0)   // 플레이어 ID는 음수일 수 있다
            { Monitor.Log($"Central FarmerId 없음: {job.Id}", LogLevel.Warn); error = "FarmerId가 없습니다."; return false; }

            switch (job.RewardType)
            {
                case KindNormal:  AddPending(State.PendingChests,        fid.Value, job.Amount); break;
                case KindPremium: AddPending(State.PendingPremiumChests, fid.Value, job.Amount); break;
                case "monster":   AddPending(State.PendingMonsters,      fid.Value, job.Amount); break;
                case KindTax:        if (!ApplyTax(out error)) return false; break;
                case KindPriceShift: ApplyPriceShiftJob(); break;
                case KindFood:
                    AddPendingItem(fid.Value, new PendingItem { ItemId = "(O)" + BoneMeatId, Amount = Math.Max(1, job.Amount), Quality = 0, Name = "뼈고기" });
                    break;
                case KindItem:
                {
                    // 게임에 없는 아이템 ID면 지급하지 않고 관리자 페이지에 '확인 필요'로 알린다.
                    Item? check = CreateGrantItem(job.ItemId, 1, job.Quality);
                    if (check == null || job.Amount <= 0)
                    {
                        error = $"게임에 없는 아이템 ID입니다: {job.ItemId}";
                        Monitor.Log($"지급 아이템 거부: {job.Id}, {error}", LogLevel.Warn);
                        return false;
                    }
                    if (!State.PendingItems.TryGetValue(fid.Value, out List<PendingItem>? list)) State.PendingItems[fid.Value] = list = new List<PendingItem>();
                    list.Add(new PendingItem { ItemId = job.ItemId!, Amount = job.Amount, Quality = job.Quality, Name = check.DisplayName });
                    break;
                }
                default: Monitor.Log($"알 수 없는 보상 종류: {job.RewardType}", LogLevel.Warn); error = $"알 수 없는 보상 종류: {job.RewardType} (게임 모드 업데이트 필요)"; return false;
            }

            RememberEvent(jobKey);
            Monitor.Log($"Central 보상 접수: {job.Id}, {job.RewardType} {job.Amount}개{(job.RewardType == KindItem ? $" ({job.ItemId})" : "")}, FarmerId={fid}", LogLevel.Info);
            if (job.RewardType is KindNormal or KindPremium or KindItem or KindFood) NotifyPending(fid.Value);
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

        // 하루 길이 (config.json의 ClockSpeed). 시간은 호스트가 흘리므로 호스트에서만 바꾸면 모두에게 적용된다.
        // 1 = 원래 속도(하루 약 14분), 0.5 = 절반 속도(약 28분), 2 = 두 배 빠르게. 0.1~10 사이로 제한한다.
        private int    defaultMsPerGameMinute = -1;
        private double appliedClockSpeed = -1;
        private void ApplyClockSpeed()
        {
            if (!Context.IsWorldReady || !Context.IsMainPlayer) return;
            double speed = config.ClockSpeed;
            if (double.IsNaN(speed) || speed <= 0) speed = 1;
            speed = Math.Clamp(speed, 0.1, 10);
            if (speed == appliedClockSpeed) return;
            if (defaultMsPerGameMinute < 0) defaultMsPerGameMinute = Game1.realMilliSecondsPerGameMinute > 0 ? Game1.realMilliSecondsPerGameMinute : 700;
            int ms = Math.Max(1, (int)Math.Round(defaultMsPerGameMinute / speed));
            Game1.realMilliSecondsPerGameMinute = ms;
            Game1.realMilliSecondsPerGameTenMinutes = ms * 10;
            appliedClockSpeed = speed;
            double dayMinutes = ms * 1200 / 60000.0;   // 오전 6시~새벽 2시 = 게임 시간 1200분
            Monitor.Log($"하루 길이 변경: ClockSpeed={speed} → 게임 1분 = {ms}ms, 하루 약 {dayMinutes:0.#}분", LogLevel.Info);
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
                if (Context.IsWorldReady && Context.IsMainPlayer)
                    hostMainThreadActions.Enqueue(() => { foodHealth = config.Food.BoneMeatHealth; foodEnergy = config.Food.BoneMeatEnergy; RefreshBoneMeatText(); BroadcastState(false); });
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
        // 몬스터 소환 금지 장소: config.json NoMonsterLocations (기본: 병원). 축제와 쓰러진 상태에서도 소환하지 않는다.
        private bool CanSpawnMonstersFor(Farmer farmer, GameLocation loc)
        {
            if (farmer.health <= 0 || farmer.passedOut || Game1.isFestival() || loc.currentEvent != null) return false;
            foreach (string name in config.NoMonsterLocations ?? new List<string>())
                if (!string.IsNullOrWhiteSpace(name) && (loc.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || loc.NameOrUniqueName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    return false;
            return true;
        }

        // 플레이어 주변에서 몬스터를 놓을 수 있는 빈 칸을 찾는다 (가까운 곳부터, 최대 반경 6칸). 없으면 null.
        private Vector2? FindMonsterSpawnTile(GameLocation loc, Vector2 center)
        {
            for (int radius = 2; radius <= 6; radius++)
            {
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    Vector2 t = new((int)center.X + random.Next(-radius, radius + 1), (int)center.Y + random.Next(-radius, radius + 1));
                    if (t == center) continue;
                    if (!loc.isTileOnMap(t) || loc.isWaterTile((int)t.X, (int)t.Y)) continue;
                    if (!loc.CanSpawnCharacterHere(t)) continue;   // 건물, 울타리, 나무, 돌, 가구 등이 있는 칸 제외
                    return t;
                }
            }
            return null;
        }

        private void AnnounceRareMonster(Farmer farmer, MonsterEntry pick, int count = 1)
        {
            MonstersFile file = monsters;
            string? template = pick.Rarity switch
            {
                "rare"     => file.RareMessage,
                "veryRare" => file.VeryRareMessage,
                _          => null
            };
            if (string.IsNullOrWhiteSpace(template)) return;

            string monsterName = (string.IsNullOrWhiteSpace(pick.Name) ? pick.Type : pick.Name) + (count > 1 ? $" {count}마리" : "");
            string text = template.Replace("{player}", farmer.Name).Replace("{monster}", monsterName);
            try
            {
                Notice(null, text);   // 플레이어 게임에서 노란 안내로 표시 (서버 채팅은 한글이 깨짐)
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
            // 하루 길이(시계 속도). 1 = 원래, 0.5 = 하루가 두 배 길어짐, 2 = 두 배 짧아짐. 서버 config.json에서 바꾸면 2초 안에 적용
            public double ClockSpeed   { get; set; } = 1.0;
            // true = 한 Steam 계정은 캐릭터 하나만. 이미 캐릭터가 있으면 빈 칸(새 캐릭터)을 고를 수 없다 (게임 서버 config.json)
            public bool   OneCharacterPerAccount { get; set; } = true;
            // 몬스터 소환 속도와 상한. 한꺼번에 너무 많이 나오면 다른 플레이어 연결이 끊긴다 (Steam 전송 한도 초과)
            public int    MonsterSpawnPerSecond  { get; set; } = 10;   // 1초에 소환하는 최대 수
            public int    MaxMonstersPerLocation { get; set; } = 50; 
            // 몬스터를 소환하지 않는 장소 이름. 그 장소에 있는 동안은 기다렸다가 나오면 소환 (기본: 병원)
            public List<string> NoMonsterLocations { get; set; } = new() { "Hospital" };  // 한 장소에 있을 수 있는 몬스터 최대 수 (넘으면 줄어들 때까지 대기)
            // true = 축제 메인 이벤트를 자동(5분 뒤)으로 시작하지 않고 /축제시작 투표로 시작. FestivalVoteRatio보다 많은 비율이 찬성하면 시작 (0.5 = 과반수)
            public bool   FestivalVote      { get; set; } = true;
            public double FestivalVoteRatio { get; set; } = 0.5;
            // 공동 목표: 공용 돈을 모아 GoalGold를 제출. 기한은 GoalDeadlineYear년차 GoalDeadlineSeason(spring/summer/fall/winter) GoalDeadlineDay일이 끝날 때까지
            public bool   GoalEnabled        { get; set; } = true;
            public long   GoalGold           { get; set; } = 150000;
            public int    GoalDeadlineYear   { get; set; } = 1;
            public string GoalDeadlineSeason { get; set; } = "winter";
            public int    GoalDeadlineDay    { get; set; } = 28;
            // 이 숫자를 바꾸면(예: 0 → 1) 공동 목표가 한 번 초기화된다 (모은 금액 0, 달성/실패 해제). 제출한 돈은 돌려주지 않는다
            public int    GoalResetCount     { get; set; } = 0;
            // 후원 보상 수치 (호스트의 값이 모든 플레이어에게 적용된다)
            public TaxConfig        Tax        { get; set; } = new();
            public PriceShiftConfig PriceShift { get; set; } = new();
            public FoodConfig       Food       { get; set; } = new();
        }

        private sealed class TaxConfig
        {
            // 세금: 징수할 때마다 MinPercent~MaxPercent 사이에서 무작위로 정한 %만큼 공용 자금을 징수한다 (0~100)
            public int MinPercent { get; set; } = 10;
            public int MaxPercent { get; set; } = 50;
        }

        private sealed class PriceShiftConfig
        {
            public int Days { get; set; } = 1;                    // 시세 변동이 유지되는 게임 내 일수
            public int MaxIncreasePercent { get; set; } = 50;     // 아이템별 최대 상승폭(%)
            public int MaxDecreasePercent { get; set; } = 50;     // 아이템별 최대 하락폭(%) (최대 90)
        }

        private sealed class FoodConfig
        {
            public int BoneMeatHealth { get; set; } = 50;         // 뼈고기를 먹으면 회복되는 체력
            public int BoneMeatEnergy { get; set; } = 100;        // 뼈고기를 먹으면 회복되는 기력
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
            public List<LinkEventDto>? LinkEvents { get; set; }
            public string? PublicBaseUrl { get; set; }
        }

        private sealed class LinkEventDto
        {
            public string FarmerId { get; set; } = "";
            public string Kind     { get; set; } = "info";
            public string Message  { get; set; } = "";
        }

        private sealed class CentralJob
        {
            public string  Id         { get; set; } = "";
            public string  SoopId     { get; set; } = "";
            public string? FarmerId   { get; set; }
            public string  RewardType { get; set; } = "";
            public int     Amount     { get; set; }
            public string  Reason     { get; set; } = "";
            public string? ItemId     { get; set; }   // rewardType = "item"
            public int     Quality    { get; set; }

            public long? GetFarmerIdLong()
                => long.TryParse(FarmerId, out long v) ? v : null;
        }

        public sealed class BridgeState
        {
            public List<string>          ProcessedEventIds    { get; set; } = new();
            public Dictionary<long, int> PendingChests        { get; set; } = new();   // 일반 상자
            public Dictionary<long, int> PendingPremiumChests { get; set; } = new();   // 고급 상자
            public Dictionary<long, int> PendingMonsters      { get; set; } = new();
            public Dictionary<long, List<PendingItem>> PendingItems { get; set; } = new();   // 관리자 지급 아이템
            // 공동 목표 (GoalFeature.cs)
            public long GoalSubmitted { get; set; }
            public bool GoalAchieved  { get; set; }
            public bool GoalFailed    { get; set; }
            public int  GoalResetApplied { get; set; }   // 마지막으로 적용한 GoalResetCount
            public PriceShiftState? PriceShift { get; set; }                                  // 진행 중인 시세 변동
        }
    }
}
