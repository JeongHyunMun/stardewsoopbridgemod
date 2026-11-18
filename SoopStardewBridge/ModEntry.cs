
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.Objects;
using StardewValley.Monsters;

namespace SoopStardewBridge
{
    internal sealed class ModEntry : Mod
    {
        private const string ChestItemId = "SoopStardewBridge_RewardChest";
        private const string MailPrefix = "SoopStardewBridge_ChestMail_";
        private const string ChestTextureAsset = "SoopStardewBridge/reward_chest";
        private const int ChestSpriteIndex = 0;
        private const int MaxMailEntries = 10000;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        // 로컬 HTTP 수신 (StardewSOOPBridge Node 브리지에서 직접 전달받는 경로)
        private readonly ConcurrentQueue<DonationEvent> incomingEvents = new();
        // Central 서버 폴링으로 받은 관리자 보상 작업
        private readonly ConcurrentQueue<CentralJob> incomingCentralJobs = new();
        private readonly HashSet<long> loggedFarmerIds = new();
        private readonly Random random = new();
        private readonly HttpClient centralHttp = new() { Timeout = TimeSpan.FromSeconds(12) };

        private BridgeConfig config = new();
        private BridgeState state = new();

        private HttpListener? listener;
        private CancellationTokenSource? listenerCancellation;
        private CancellationTokenSource? centralCancellation;

        public override void Entry(IModHelper helper)
        {
            config = Helper.ReadConfig<BridgeConfig>();

            if (string.IsNullOrWhiteSpace(config.AuthToken))
            {
                config.AuthToken = Guid.NewGuid().ToString("N");
                Helper.WriteConfig(config);
            }

            state = Helper.Data.ReadJsonFile<BridgeState>("state.json")
                ?? new BridgeState();

            Helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            Helper.Events.GameLoop.DayStarted += OnDayStarted;
            Helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            Helper.Events.Input.ButtonPressed += OnButtonPressed;
            Helper.Events.Content.AssetRequested += OnAssetRequested;

            StartHttpListener();
            StartCentralPolling();

            Monitor.Log("SOOP Stardew Bridge가 시작되었습니다.", LogLevel.Info);
            Monitor.Log($"로컬 후원 수신 주소: http://127.0.0.1:{config.Port}/event", LogLevel.Info);

            if (!string.IsNullOrWhiteSpace(config.CentralServerUrl))
                Monitor.Log($"Central 서버 연결: {config.CentralServerUrl}", LogLevel.Info);
            else
                Monitor.Log("Central 서버 미설정. config.json에 CentralServerUrl과 AgentToken을 추가하면 관리자 보상을 받을 수 있습니다.", LogLevel.Info);
        }

        // F6 테스트: 현재 플레이어에게 보상 상자 1개를 우편으로 등록합니다.
        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsWorldReady || !Context.IsMainPlayer)
                return;

            if (e.Button != SButton.F6)
                return;

            Farmer farmer = Game1.player;
            long farmerId = farmer.UniqueMultiplayerID;

            AddPending(state.PendingChests, farmerId, 1);
            SaveState();

            Monitor.Log($"[F6 테스트] {farmer.Name}(FarmerId={farmerId})에게 보상 상자 1개를 지급 요청했습니다.", LogLevel.Info);
            ProcessPendingRewards();
        }

        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            state = Helper.Data.ReadJsonFile<BridgeState>("state.json") ?? state;
            loggedFarmerIds.Clear();
            SaveState();
            LogOnlineFarmers();
            ProcessPendingRewards();

            Monitor.Log(
                $"처리 이벤트: {state.ProcessedEventIds.Count}, " +
                $"대기 상자 대상: {state.PendingChests.Count}, " +
                $"대기 몬스터 대상: {state.PendingMonsters.Count}",
                LogLevel.Info
            );
        }

        private void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            ProcessPendingRewards();
        }

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady)
                return;

            if (e.IsMultipleOf(60))
                LogOnlineFarmers();

            // 로컬 HTTP 수신 이벤트 처리
            while (incomingEvents.TryDequeue(out DonationEvent? donation))
            {
                if (donation != null)
                    ProcessDonation(donation);
            }

            // Central 서버 폴링 작업 처리
            while (incomingCentralJobs.TryDequeue(out CentralJob? job))
            {
                if (job != null)
                {
                    bool processed = ProcessCentralJob(job);
                    if (!string.IsNullOrWhiteSpace(job.Id))
                        _ = AcknowledgeCentralJobAsync(job.Id, processed);
                }
            }

            if (e.IsMultipleOf(60))
                ProcessPendingRewards();
        }

        private void LogOnlineFarmers()
        {
            foreach (Farmer farmer in Game1.getOnlineFarmers())
            {
                long farmerId = farmer.UniqueMultiplayerID;
                if (!loggedFarmerIds.Add(farmerId))
                    continue;
                Monitor.Log($"접속 플레이어 확인: Name={farmer.Name}, FarmerId={farmerId}", LogLevel.Info);
            }
        }

        private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
        {
            if (e.NameWithoutLocale.IsEquivalentTo(ChestTextureAsset))
            {
                e.LoadFromModFile<Texture2D>("assets/reward_chest.png", AssetLoadPriority.Exclusive);
                return;
            }

            if (e.NameWithoutLocale.IsEquivalentTo("Data/Objects"))
            {
                e.Edit(asset =>
                {
                    var data = asset.AsDictionary<string, ObjectData>().Data;
                    data[ChestItemId] = new ObjectData
                    {
                        Name = ChestItemId,
                        DisplayName = "SOOP 보상 상자",
                        Description = "SOOP 후원으로 받은 특별한 보상 상자.",
                        Type = "Basic",
                        Category = -8,
                        Price = 0,
                        Texture = ChestTextureAsset,
                        SpriteIndex = ChestSpriteIndex
                    };
                });
                return;
            }

            if (e.NameWithoutLocale.IsEquivalentTo("Data/mail"))
            {
                e.Edit(asset =>
                {
                    var data = asset.AsDictionary<string, string>().Data;
                    for (int i = 1; i <= MaxMailEntries; i++)
                    {
                        string mailId = MailPrefix + i;
                        if (data.ContainsKey(mailId)) continue;
                        data[mailId] =
                            "안녕하세요!^^" +
                            "SOOP 후원 보상 상자를 보내드립니다." +
                            "^^후원해 주셔서 감사합니다!" +
                            "%item id (O)" + ChestItemId +
                            " 1 %%[#]SOOP 보상 상자";
                    }
                });
            }
        }

        // ── Central 서버 폴링 ──────────────────────────────────────────────────
        // 게임 PC에서 Central로 아웃바운드 폴링. 스트리머 측 포트 포워딩 불필요.

        private void StartCentralPolling()
        {
            if (string.IsNullOrWhiteSpace(config.CentralServerUrl) ||
                string.IsNullOrWhiteSpace(config.AgentToken))
                return;

            centralCancellation = new CancellationTokenSource();
            _ = CentralPollingLoopAsync(centralCancellation.Token);
            Monitor.Log("Central 서버 폴링을 시작했습니다.", LogLevel.Info);
        }

        private async Task CentralPollingLoopAsync(CancellationToken ct)
        {
            string baseUrl = config.CentralServerUrl.TrimEnd('/');
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/agent/poll");
                    req.Headers.Add("X-Agent-Token", config.AgentToken);
                    req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    using HttpResponseMessage resp = await centralHttp.SendAsync(req, ct).ConfigureAwait(false);
                    string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        CentralPollResponse? poll = JsonSerializer.Deserialize<CentralPollResponse>(json, JsonOptions);
                        if (poll?.Job != null)
                            incomingCentralJobs.Enqueue(poll.Job);
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

                try { await Task.Delay(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false); }
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
                    status = processed ? "COMPLETED" : "REVIEW",
                    message = processed ? "SMAPI가 보상을 처리했습니다." : "FarmerId가 없거나 플레이어가 오프라인 상태입니다."
                }), Encoding.UTF8, "application/json");
                using HttpResponseMessage resp = await centralHttp.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    Monitor.Log($"Central ack 전송 실패: job={jobId}, HTTP={(int)resp.StatusCode}", LogLevel.Warn);
            }
            catch (Exception ex) { Monitor.Log($"Central ack 오류: job={jobId}, {ex.Message}", LogLevel.Warn); }
        }

        // ── 로컬 HTTP 수신 ────────────────────────────────────────────────────

        private void StartHttpListener()
        {
            if (config.Port < 1 || config.Port > 65535)
            {
                Monitor.Log("config.json의 Port 값이 잘못되었습니다.", LogLevel.Error);
                return;
            }

            listenerCancellation = new CancellationTokenSource();
            listener = new HttpListener();
            listener.Prefixes.Add($"http://+:{config.Port}/");

            try
            {
                listener.Start();
                _ = ListenLoopAsync(listener, listenerCancellation.Token);
            }
            catch (Exception ex)
            {
                Monitor.Log($"HTTP 수신부 시작 실패: {ex}", LogLevel.Error);
                listener.Close();
                listener = null;
            }
        }

        private async Task ListenLoopAsync(HttpListener httpListener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && httpListener.IsListening)
            {
                HttpListenerContext context;
                try { context = await httpListener.GetContextAsync().ConfigureAwait(false); }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) { Monitor.Log($"HTTP 수신 오류: {ex.Message}", LogLevel.Warn); continue; }
                _ = HandleHttpRequestAsync(context);
            }
        }

        private async Task HandleHttpRequestAsync(HttpListenerContext context)
        {
            try
            {
                HttpListenerRequest request = context.Request;
                HttpListenerResponse response = context.Response;

                if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(response, 405, "{\"error\":\"POST required\"}");
                    return;
                }

                if (!string.Equals(request.Url?.AbsolutePath, "/event", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(response, 404, "{\"error\":\"not found\"}");
                    return;
                }

                string suppliedToken = request.Headers["X-SOOP-Token"] ?? "";
                if (!FixedTimeEquals(suppliedToken, config.AuthToken))
                {
                    await WriteResponseAsync(response, 401, "{\"error\":\"unauthorized\"}");
                    return;
                }

                if (request.ContentLength64 > 64 * 1024)
                {
                    await WriteResponseAsync(response, 413, "{\"error\":\"payload too large\"}");
                    return;
                }

                string body;
                using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                    body = await reader.ReadToEndAsync().ConfigureAwait(false);

                DonationEvent? donation;
                try { donation = JsonSerializer.Deserialize<DonationEvent>(body, JsonOptions); }
                catch (JsonException)
                {
                    await WriteResponseAsync(response, 400, "{\"error\":\"invalid json\"}");
                    return;
                }

                if (donation == null || string.IsNullOrWhiteSpace(donation.EventId) ||
                    string.IsNullOrWhiteSpace(donation.SoopId) || donation.Balloons <= 0)
                {
                    await WriteResponseAsync(response, 400, "{\"error\":\"eventId, soopId and positive balloons are required\"}");
                    return;
                }

                incomingEvents.Enqueue(donation);
                await WriteResponseAsync(response, 202, "{\"accepted\":true}");
            }
            catch (Exception ex)
            {
                Monitor.Log($"HTTP 요청 처리 오류: {ex.Message}", LogLevel.Warn);
                try { await WriteResponseAsync(context.Response, 500, "{\"error\":\"internal error\"}"); } catch { }
            }
        }

        private static async Task WriteResponseAsync(HttpListenerResponse response, int statusCode, string content)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            response.Close();
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        // ── 보상 처리 ─────────────────────────────────────────────────────────

        // 로컬 브리지(StardewSOOPBridge)에서 받은 별풍선 이벤트 처리
        private void ProcessDonation(DonationEvent donation)
        {
            string eventId = donation.EventId.Trim();
            string soopId = donation.SoopId.Trim();

            if (state.ProcessedEventIds.Contains(eventId))
            {
                Monitor.Log($"중복 이벤트 무시: {eventId}", LogLevel.Warn);
                return;
            }

            if (donation.Balloons <= 0)
            {
                Monitor.Log($"잘못된 후원 수량: SOOP={soopId}, 별풍선={donation.Balloons}", LogLevel.Warn);
                return;
            }

            // 로컬 경로는 FarmerId를 donation 페이로드에서 받거나 온라인 플레이어를 soopId 이름으로 매칭.
            // FarmerId가 명시적으로 전달된 경우 우선 사용한다.
            long? targetFarmerId = donation.FarmerId > 0 ? donation.FarmerId : null;

            if (targetFarmerId == null)
            {
                Monitor.Log($"로컬 경로: FarmerId 없음 (soopId={soopId}). Central 경로 사용을 권장합니다.", LogLevel.Warn);
                RememberEvent(eventId);
                return;
            }

            int balloons = donation.Balloons;
            int chestCount = 0;
            int monsterCount = 0;

            if (config.TestMode)
            {
                if (balloons == 1) monsterCount = 1;
                else if (balloons == 2) chestCount = 1;
                else if (balloons == 3) chestCount = 11;
            }
            else
            {
                if (balloons >= 33) monsterCount = 1;
                if (balloons == 1000) chestCount = 11;
                else if (balloons >= 100 && balloons < 1000) chestCount = balloons / 100;
            }

            if (chestCount == 0 && monsterCount == 0)
            {
                RememberEvent(eventId);
                Monitor.Log($"보상 기준 미달: SOOP={soopId}, 별풍선={balloons}", LogLevel.Info);
                return;
            }

            if (chestCount > 0) AddPending(state.PendingChests, targetFarmerId.Value, chestCount);
            if (monsterCount > 0) AddPending(state.PendingMonsters, targetFarmerId.Value, monsterCount);
            RememberEvent(eventId);

            Monitor.Log($"후원 이벤트 접수: ID={eventId}, SOOP={soopId}, 별풍선={balloons}, 상자={chestCount}, 몬스터={monsterCount}, FarmerId={targetFarmerId}", LogLevel.Info);
            ProcessPendingRewards();
        }

        // Central 서버 폴링으로 받은 보상 작업 처리
        // FarmerId는 Central db.json의 streamer.farmerId에서 내려옵니다.
        private bool ProcessCentralJob(CentralJob job)
        {
            string jobKey = "central:" + job.Id.Trim();

            if (state.ProcessedEventIds.Contains(jobKey))
            {
                Monitor.Log($"Central 작업 중복 무시: {job.Id}", LogLevel.Warn);
                return true;
            }

            long? farmerIdLong = job.GetFarmerIdLong();

            if (farmerIdLong == null || farmerIdLong <= 0)
            {
                Monitor.Log($"Central 작업 처리 불가: FarmerId 없음 (jobId={job.Id}, soopId={job.SoopId}). 관리자 페이지에서 FarmerId를 등록하세요.", LogLevel.Warn);
                return false;
            }

            if (job.RewardType == "chest")
                AddPending(state.PendingChests, farmerIdLong.Value, job.Amount);
            else if (job.RewardType == "monster")
                AddPending(state.PendingMonsters, farmerIdLong.Value, job.Amount);
            else
            {
                Monitor.Log($"Central 작업: 알 수 없는 보상 종류 '{job.RewardType}'", LogLevel.Warn);
                return false;
            }

            RememberEvent(jobKey);
            Monitor.Log($"Central 보상 접수: jobId={job.Id}, SOOP={job.SoopId}, 종류={job.RewardType}, 수량={job.Amount}, FarmerId={farmerIdLong}", LogLevel.Info);
            ProcessPendingRewards();
            return true;
        }

        private void RememberEvent(string eventId)
        {
            state.ProcessedEventIds.Add(eventId);
            while (state.ProcessedEventIds.Count > 50000)
                state.ProcessedEventIds.RemoveAt(0);
            SaveState();
        }

        private static void AddPending(Dictionary<long, int> dictionary, long farmerId, int count)
        {
            if (count <= 0) return;
            dictionary.TryGetValue(farmerId, out int existing);
            dictionary[farmerId] = existing + count;
        }

        private void ProcessPendingRewards()
        {
            if (!Context.IsWorldReady)
                return;

            foreach (var entry in state.PendingChests.ToArray())
            {
                long farmerId = entry.Key;
                int pending = entry.Value;

                if (pending <= 0) { state.PendingChests.Remove(farmerId); continue; }

                Farmer? farmer = FindOnlineFarmer(farmerId);
                if (farmer == null) continue;

                int sent = 0;
                while (sent < pending && state.NextMailNumber < MaxMailEntries)
                {
                    int nextNumber = state.NextMailNumber + 1;
                    string mailId = MailPrefix + nextNumber;
                    state.NextMailNumber = nextNumber;
                    if (farmer.hasOrWillReceiveMail(mailId)) continue;
                    farmer.mailbox.Add(mailId);
                    sent++;
                    state.PendingChests[farmerId] = pending - sent;
                    SaveState();
                }

                int remaining = pending - sent;
                if (remaining <= 0) state.PendingChests.Remove(farmerId);
                else state.PendingChests[farmerId] = remaining;

                if (sent > 0) Monitor.Log($"보상 우편 등록: FarmerId={farmerId}, 수량={sent}", LogLevel.Info);
                if (remaining > 0 && state.NextMailNumber >= MaxMailEntries)
                    Monitor.Log("편지 ID 한도에 도달했습니다. 남은 보상은 대기 상태입니다.", LogLevel.Warn);
            }

            foreach (var entry in state.PendingMonsters.ToArray())
            {
                long farmerId = entry.Key;
                int pending = entry.Value;

                if (pending <= 0) { state.PendingMonsters.Remove(farmerId); continue; }

                Farmer? farmer = FindOnlineFarmer(farmerId);
                if (farmer?.currentLocation == null) continue;

                int spawned = 0;
                while (spawned < pending)
                {
                    Vector2 tile = farmer.Tile;
                    Vector2 spawnTile = new(tile.X + random.Next(-3, 4), tile.Y + random.Next(-3, 4));
                    farmer.currentLocation.characters.Add(new GreenSlime(spawnTile * 64f));
                    spawned++;
                }

                state.PendingMonsters.Remove(farmerId);
                Monitor.Log($"몬스터 소환 완료: FarmerId={farmerId}, 수량={spawned}", LogLevel.Info);
            }

            SaveState();
        }

        private static Farmer? FindOnlineFarmer(long farmerId)
        {
            return Game1.getOnlineFarmers().FirstOrDefault(f => f.UniqueMultiplayerID == farmerId);
        }

        private void SaveState()
        {
            Helper.Data.WriteJsonFile("state.json", state);
        }

        // ── 데이터 클래스 ─────────────────────────────────────────────────────

        private sealed class BridgeConfig
        {
            public int Port { get; set; } = 8765;
            public string AuthToken { get; set; } = "";
            // Central 서버 연결 설정 (선택)
            public string CentralServerUrl { get; set; } = "";
            public string AgentToken { get; set; } = "";
            // true=테스트 기준(1/2/3개), false=실제 기준(33/100/1000개)
            public bool TestMode { get; set; } = true;
        }

        private sealed class DonationEvent
        {
            public string EventId { get; set; } = "";
            public string SoopId { get; set; } = "";
            public int Balloons { get; set; }
            public long FarmerId { get; set; }
        }

        private sealed class CentralPollResponse
        {
            public bool Ok { get; set; }
            public CentralJob? Job { get; set; }
        }

        private sealed class CentralJob
        {
            public string Id { get; set; } = "";
            public string SoopId { get; set; } = "";
            // JSON에서 string으로 수신 후 long으로 파싱 (JS Number 정밀도 손실 방지)
            public string? FarmerId { get; set; }
            public string RewardType { get; set; } = "";
            public int Amount { get; set; }
            public string Reason { get; set; } = "";

            public long? GetFarmerIdLong()
            {
                if (string.IsNullOrWhiteSpace(FarmerId)) return null;
                return long.TryParse(FarmerId, out long v) ? v : null;
            }
        }

        private sealed class BridgeState
        {
            public List<string> ProcessedEventIds { get; set; } = new();
            public Dictionary<long, int> PendingChests { get; set; } = new();
            public Dictionary<long, int> PendingMonsters { get; set; } = new();
            public int NextMailNumber { get; set; }
        }
    }
}
