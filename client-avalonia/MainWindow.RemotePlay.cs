// Remote Play tab — pairing orchestration + chiaki-ng launch.
//
// Flow: "Pair & Connect" asks the payload for a fresh PIN + the console's PSN
// account id (CMD_REMOTEPLAY / rp|pin), performs the Remote Play registration
// handshake from this PC (UDP probe + encrypted TCP regist on :9295 — see
// RemotePlay.cs), stores the returned credentials locally, mirrors them into
// chiaki-ng's QSettings, then launches `chiaki.exe stream <nickname> <ip>`.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace PS5Upload
{
    public partial class MainWindow
    {
        private const string RpStoreFile = "ps5suite_remoteplay.json";
        private RemotePlayRegistration? _rpRegistration;
        private string? _rpChiakiPath;
        private bool _rpBusy;

        // ---------------- store ----------------

        private void RpLoadStore()
        {
            try
            {
                if (!File.Exists(RpStoreFile)) return;
                var doc = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(RpStoreFile));
                if (doc == null) return;
                if (doc.TryGetValue("chiaki_path", out var cp)) _rpChiakiPath = cp?.ToString();
                if (doc.TryGetValue("registration", out var reg) && reg is System.Text.Json.JsonElement je)
                    _rpRegistration = System.Text.Json.JsonSerializer.Deserialize<RemotePlayRegistration>(je.GetRawText());
            }
            catch { }
        }

        private void RpSaveStore()
        {
            try
            {
                var doc = new Dictionary<string, object>
                {
                    ["chiaki_path"] = _rpChiakiPath ?? "",
                    ["registration"] = _rpRegistration!
                };
                File.WriteAllText(RpStoreFile,
                    System.Text.Json.JsonSerializer.Serialize(doc, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        // ---------------- UI refresh ----------------

        private async void RpRefresh_Click(object? sender, RoutedEventArgs e) => await RpRefreshAsync();

        private async Task RpRefreshAsync()
        {
            if (!_protocol.IsConnected) { RpStateText.Text = "Not connected to PS5"; return; }
            var (ok, json) = await _protocol.RemotePlayCommandAsync("status");
            if (!ok) { RpStateText.Text = $"⚠️ {json}"; return; }
            var st = RemotePlayStatus.FromJson(json);
            if (st == null) { RpStateText.Text = "⚠️ could not parse status"; return; }
            RpApplyStatus(st);
        }

        private void RpApplyStatus(RemotePlayStatus st)
        {
            string stateLabel = st.State switch
            {
                "waiting" => $"🟡 Waiting for device (PIN active, {st.SecondsLeft}s left)",
                "paired" => "🟢 Device paired",
                "failed" => "🔴 Pairing failed",
                "timeout" => "🟠 PIN expired",
                "busy" => "🟡 Busy",
                _ => "⚪ Idle — ready to pair"
            };
            RpStateText.Text = stateLabel;

            RpAccountText.Text = string.IsNullOrEmpty(st.AccountId)
                ? "Account: —"
                : $"Account: {st.AccountId}  ({st.AccountType}{(string.IsNullOrEmpty(st.UserName) ? "" : $", {st.UserName}")})";

            var svc = st.ServiceEnabled ? "on" : "off";
            var usr = st.HasPerUser ? (st.UserEnabled ? "allowed" : "blocked") : "n/a";
            RpServiceText.Text = $"Service: {svc} · user: {usr} · symbols: {(st.SymbolsOk ? "ok" : "missing")} · regmgr: {(st.RegmgrOk ? "ok" : "missing")}";

            if (!string.IsNullOrEmpty(st.Error))
            {
                RpErrorText.Text = $"⚠️ {st.Error}";
                RpErrorText.IsVisible = true;
            }
            else RpErrorText.IsVisible = false;

            RpEnableUserButton.IsVisible = st.HasPerUser && st.ServiceEnabled && !st.UserEnabled;
            RpDevicesText.Text = st.DeviceCount > 0 ? $"{st.DeviceCount} paired" : "";

            // PIN panel mirrors payload state (e.g. PIN issued from web UI too)
            bool waiting = st.State == "waiting" && !string.IsNullOrEmpty(st.Pin);
            RpPinPanel.IsVisible = waiting;
            if (waiting)
            {
                RpPinText.Text = st.Pin;
                RpPinCountdown.Text = $"expires in {st.SecondsLeft}s";
            }
            RpCancelButton.IsVisible = waiting;

            RpDevicesList.Text = st.Devices.Count == 0
                ? "No paired devices yet."
                : string.Join("\n", st.Devices.ConvertAll(d =>
                    $"  slot {d.Slot,2}: user_id {d.UserId} · client_type {d.ClientType}"));

            if (_rpRegistration != null)
                RpOurRegText.Text = $"This PC is registered as \"{_rpRegistration.ServerNickname}\" " +
                                    $"(mac {_rpRegistration.ServerMacHex}, {_rpRegistration.RegisteredUtc:yyyy-MM-dd HH:mm} UTC).";
            else
                RpOurRegText.Text = "This PC is not paired yet.";

            RpConnectButton.IsEnabled = _rpRegistration != null;
        }

        // ---------------- enable toggles ----------------

        private async void RpEnable_Click(object? sender, RoutedEventArgs e)
        {
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5", "Remote Play"); return; }
            var (ok, json) = await _protocol.RemotePlayCommandAsync("enable");
            var st = RemotePlayStatus.FromJson(json);
            if (st != null) RpApplyStatus(st);
            RpPairStatus.Text = ok
                ? "✅ Registry flag set (rp_enable=1). Note: the console-side switch is Settings → System → Remote Play on the PS5 itself — that toggle is what wakes the daemon."
                : $"❌ {json}";
            Log(ok ? "✅ Remote Play registry flag set" : $"❌ {json}");
        }

        private async void RpEnableUser_Click(object? sender, RoutedEventArgs e)
        {
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5", "Remote Play"); return; }
            var (ok, json) = await _protocol.RemotePlayCommandAsync("enableuser");
            var st = RemotePlayStatus.FromJson(json);
            if (st != null) RpApplyStatus(st);
            Log(ok ? "✅ Remote Play allowed for this user" : $"❌ {json}");
        }

        // ---------------- pair & connect ----------------

        private async void RpPairConnect_Click(object? sender, RoutedEventArgs e)
        {
            if (_rpBusy) return;
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5", "Remote Play"); return; }
            if (string.IsNullOrEmpty(_ps5IpAddress)) { await ShowMessageAsync("No PS5 IP known", "Remote Play"); return; }

            if (!ChiakiStreamSession.EngineAvailable)
            {
                await ShowMessageAsync(
                    "Streaming engine not found.\n\nps5chiaki.dll is missing next to PS5Suite.exe — reinstall or restore the native engine, then retry.",
                    "Remote Play");
                return;
            }

            _rpBusy = true;
            RpProgress.IsVisible = true;
            RpPairConnectButton.IsEnabled = false;
            try
            {
                // 1. ask the payload for a fresh PIN (+ account id)
                RpPairStatus.Text = "Requesting PIN from console…";
                var (ok, json) = await _protocol.RemotePlayCommandAsync("pin", 45000);
                var st = RemotePlayStatus.FromJson(json);
                if (st != null) RpApplyStatus(st);
                if (!ok || st == null || st.State != "waiting" || string.IsNullOrEmpty(st.Pin))
                {
                    RpPairStatus.Text = $"❌ PIN request failed: {st?.Error ?? json}";
                    return;
                }
                uint pin = uint.Parse(st.Pin);
                string accountB64 = st.AccountId;
                RpPairStatus.Text = $"PIN {st.Pin} issued — registering this PC…";

                // 2. quick probe: console should answer SRC3 while pairing mode is up
                bool found = await RemotePlayRegist.SearchAsync(_ps5IpAddress, CancellationToken.None);
                if (!found)
                    Log("⚠️ No RES3 probe answer (console may still accept regist — continuing)");

                // 3. regist handshake from this PC
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                var res = await RemotePlayRegist.RegisterAsync(_ps5IpAddress, accountB64, pin, cts.Token);
                if (!res.Ok || res.Registration == null)
                {
                    RpPairStatus.Text = $"❌ Registration failed: {res.Error}";
                    // let the payload know the attempt is over (its poll shows the console-side reason)
                    await _protocol.RemotePlayCommandAsync("poll");
                    return;
                }
                _rpRegistration = res.Registration;
                RpSaveStore();
                RpPairStatus.Text = $"✅ Paired with \"{res.Registration.ServerNickname}\" — opening stream…";

                var (pok, pjson) = await _protocol.RemotePlayCommandAsync("poll");
                var pst = RemotePlayStatus.FromJson(pjson);
                if (pst != null) RpApplyStatus(pst);

                var wnd = new RemotePlayWindow(res.Registration, _ps5IpAddress);
                wnd.Show();
                Log("🎬 Remote Play stream opened (in-process engine)");
            }
            catch (Exception ex)
            {
                RpPairStatus.Text = $"❌ {ex.Message}";
            }
            finally
            {
                _rpBusy = false;
                RpProgress.IsVisible = false;
                RpPairConnectButton.IsEnabled = true;
            }
        }

        private void RpConnect_Click(object? sender, RoutedEventArgs e)
        {
            if (_rpRegistration == null) { RpPairStatus.Text = "Not paired — use Pair & Connect first"; return; }
            if (!ChiakiStreamSession.EngineAvailable) { RpPairStatus.Text = "Streaming engine (ps5chiaki) missing next to PS5Suite"; return; }
            var wnd = new RemotePlayWindow(_rpRegistration, _ps5IpAddress);
            wnd.Show();
            Log("🎬 Remote Play stream opened");
        }

        private async void RpCancel_Click(object? sender, RoutedEventArgs e)
        {
            if (!_protocol.IsConnected) return;
            var (_, json) = await _protocol.RemotePlayCommandAsync("cancel");
            var st = RemotePlayStatus.FromJson(json);
            if (st != null) RpApplyStatus(st);
            Log("⏹ Remote Play pairing cancelled");
        }

        // ---------------- streaming engine ----------------

        private void RpChiakiDetect_Click(object? sender, RoutedEventArgs e)
        {
            if (ChiakiStreamSession.EngineAvailable)
            {
                RpChiakiPathText.Text = ChiakiStreamSession.EnginePath;
                RpPairStatus.Text = "✅ Streaming engine present";
            }
            else RpPairStatus.Text = "❌ ps5chiaki.dll missing — reinstall PS5 Suite";
        }
    }
}
