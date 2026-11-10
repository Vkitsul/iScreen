using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using IScreen.Host;
using IScreen.Host.Capture;
using IScreen.Host.Encode;
using IScreen.Host.Streaming;

namespace IScreen.Tray;

/// <summary>Runs the host in the background behind a notification-area icon, Parsec style.</summary>
sealed class TrayContext : ApplicationContext
{
    readonly HostConfig _config = HostConfig.LoadOrCreate();
    readonly ContextMenuStrip _menu = new();
    readonly ToolStripMenuItem _status = new() { Enabled = false };
    readonly ToolStripMenuItem _pin = new();
    readonly ToolStripMenuItem _screens = new("Schermo da trasmettere");
    readonly ToolStripMenuItem _autoStart = new("Avvia con Windows");
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    readonly Icon _idleIcon = TrayIcons.Create(connected: false);
    readonly Icon _connectedIcon = TrayIcons.Create(connected: true);
    readonly CancellationTokenSource _cts = new();
    readonly SynchronizationContext _ui;
    readonly NotifyIcon _icon;
    readonly ControlServer _server;
    readonly PowerEvents _power = new();
    readonly bool _afterGpuRestart;
    readonly bool _fromService; // run by the iScreen service as SYSTEM (admin mode)
    readonly long _startedMs = Environment.TickCount64;
    List<MonitorInfo> _monitors;
    string? _client;
    long _lastFrames, _lastBytes;
    bool _quit;

    /// <summary>Set before quitting when the GPU was lost: Program then starts a new iScreen.</summary>
    public bool RestartRequested { get; private set; }

    /// <param name="afterGpuRestart">This process replaces one that lost the GPU.</param>
    /// <param name="fromService">Started by the iScreen service: SYSTEM, on the user's desktop.</param>
    public TrayContext(IPAddress bind, bool applyAutoStartDefault, bool afterGpuRestart, bool fromService)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _afterGpuRestart = afterGpuRestart;
        _fromService = fromService;
        if (fromService) ListenToService();
        _monitors = Monitors.List();
        int monitor = Monitors.Find(_monitors, _config.Monitor);

        _server = new ControlServer(_config, monitor,
            new SessionOptions(StreamMode.Yuv444Packed, new EncoderSettings(), Pattern: false), allowInput: true, bind);
        _server.ClientChanged += address => _ui.Post(_ => OnClientChanged(address), null);
        _server.GpuLost += reason => _ui.Post(_ => OnGpuLost(reason), null);

        BuildMenu();
        _icon = new NotifyIcon { Icon = _idleIcon, Text = "iScreen", ContextMenuStrip = _menu, Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            // NotifyIcon only opens its menu on right click; open it on left click too.
            if (e.Button == MouseButtons.Left)
                typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(_icon, null);
        };

        new Thread(RunServer) { IsBackground = true, Name = "server" }.Start();
        _power.Suspending += OnSuspending;
        _power.Resumed += () => Console.WriteLine("PC resumed");

        if (applyAutoStartDefault && !fromService && !_config.AutoStartConfigured)
        {
            AutoStart.Enabled = true; // like Parsec: on by default, can be turned off from the menu
            _config.AutoStartConfigured = true;
            _config.Save();
        }
        _autoStart.Checked = StartsWithWindows;

        _timer.Tick += (_, _) => UpdateStatus();
        _timer.Start();
        UpdateStatus();
        string screen = _monitors.ElementAtOrDefault(monitor) is { } m ? Label(m, details: false) : "?";
        if (afterGpuRestart)
            _icon.ShowBalloonTip(8000, "iScreen riavviato",
                "L'encoder video della GPU si era bloccato. Ricollegati dall'app iScreen sul Mac.", ToolTipIcon.Warning);
        else
            _icon.ShowBalloonTip(5000, fromService ? "iScreen è attivo (anche app da amministratore)" : "iScreen è attivo",
                $"PIN {_config.Pin} · trasmetto {screen}.\nCollegati dall'app iScreen sul Mac.", ToolTipIcon.Info);
    }

    /// <summary>
    /// As SYSTEM: the service asks us to close through a named event, and Explorer must still reach the
    /// tray icon, although Windows filters messages from lower-integrity processes (UIPI).
    /// </summary>
    void ListenToService()
    {
        var quit = new EventWaitHandle(false, EventResetMode.ManualReset, SystemService.QuitEventName(Environment.ProcessId));
        ThreadPool.RegisterWaitForSingleObject(quit, (_, _) => _ui.Post(_ => Quit(), null), null,
            Timeout.Infinite, executeOnlyOnce: true);
        ChangeWindowMessageFilter(RegisterWindowMessage("TaskbarCreated"), MSGFLT_ADD); // icon back after Explorer restarts
        ChangeWindowMessageFilter(WM_TRAYMOUSEMESSAGE, MSGFLT_ADD);
    }

    /// <summary>The per-user Run key, or the service's start type when the service runs us.</summary>
    bool StartsWithWindows
    {
        get => _fromService ? AdminMode.StartsWithWindows : AutoStart.Enabled;
        set
        {
            if (_fromService) AdminMode.StartsWithWindows = value;
            else AutoStart.Enabled = value;
        }
    }

    /// <summary>Installs the service (UAC prompt), which then runs iScreen as SYSTEM in our place.</summary>
    void EnableAdminMode()
    {
        var answer = MessageBox.Show(
            "Per usare dal Mac anche le finestre aperte come amministratore e rispondere alle richieste di " +
            "Controllo account utente, iScreen deve girare come servizio di Windows, come fa Parsec. " +
            "Verrà copiato in Programmi e le sue porte saranno aperte nel firewall per la rete privata.\n\n" +
            "Fallo davanti al PC: Windows ti chiederà il permesso, e dal Mac quella richiesta non si vede. " +
            "Poi iScreen riparte da solo; il Mac andrà ricollegato.",
            "iScreen", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
        if (answer != DialogResult.OK) return;
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"{AdminMode.InstallFlag} {Environment.ProcessId}")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) // ERROR_CANCELLED: "No" on the UAC prompt
        {
            return;
        }
        Quit(); // the installer waits for us to free the ports, then starts the service
    }

    void DisableAdminMode()
    {
        var answer = MessageBox.Show(
            "iScreen tornerà a girare come una normale app: dal Mac non potrai più usare le finestre da " +
            "amministratore né rispondere alle richieste di Controllo account utente.",
            "iScreen", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer != DialogResult.OK) return;
        // As SYSTEM we may remove the service ourselves: the uninstaller stops it, which closes us.
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, AdminMode.UninstallFlag) { UseShellExecute = false });
    }

    void OpenLog()
    {
        // Never a SYSTEM Notepad: its file dialog would give the user SYSTEM rights.
        if (_fromService) Sessions.StartAsUser(Sessions.Own, $"notepad.exe \"{HostConfig.LogPath}\"");
        else Process.Start(new ProcessStartInfo(HostConfig.LogPath) { UseShellExecute = true });
    }

    const uint MSGFLT_ADD = 1, WM_TRAYMOUSEMESSAGE = 0x0400 + 1024; // WinForms' NotifyIcon callback message

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    static extern bool ChangeWindowMessageFilter(uint message, uint flag);

    /// <summary>
    /// The GPU stopped working for this process (AMF stalled, then DXGI showed no monitor, while a new
    /// process saw them all): quit and let Program start a fresh iScreen. Not again within 10 minutes of
    /// such a restart: if the new process fails the same way the GPU itself is stuck, and only restarting
    /// the PC helps.
    /// </summary>
    void OnGpuLost(string reason)
    {
        if (_quit) return;
        if (_afterGpuRestart && Environment.TickCount64 - _startedMs < 10 * 60_000)
        {
            Console.WriteLine($"GPU lost again soon after a restart ({reason}): not restarting");
            _icon.ShowBalloonTip(15000, "La GPU non risponde",
                "L'encoder video della Radeon si è bloccato di nuovo. Riavvia il PC, poi ricollegati dal Mac.",
                ToolTipIcon.Error);
            return;
        }
        Console.WriteLine($"GPU lost ({reason}): restarting iScreen");
        RestartRequested = true;
        Quit();
    }

    /// <summary>
    /// A session left open across sleep got stuck on wake (GPU encoder and capture don't survive it):
    /// close it before suspending (and restore the resolution); the Mac reconnects afterwards.
    /// Windows only waits a moment for us, so don't hold the suspend up for long.
    /// </summary>
    void OnSuspending()
    {
        Console.WriteLine("PC going to sleep");
        var done = Task.Run(() => _server.DisconnectClient("PC going to sleep"));
        if (!done.Wait(TimeSpan.FromSeconds(1.5))) Console.WriteLine("session still closing when the PC went to sleep");
    }

    void RunServer()
    {
        try
        {
            _server.Run(_cts.Token);
        }
        catch (SocketException e)
        {
            Console.WriteLine($"Server failed: {e.Message}");
            _ui.Post(_ => _icon.ShowBalloonTip(10000, "iScreen non può partire",
                "La porta 52700 è già in uso: forse iscreen-host è aperto in un terminale. Chiudilo e riavvia iScreen.",
                ToolTipIcon.Error), null);
        }
    }

    void BuildMenu()
    {
        _status.Font = new Font(_menu.Font, FontStyle.Bold);
        _pin.Click += (_, _) =>
        {
            Clipboard.SetText(_config.Pin);
            _icon.ShowBalloonTip(2000, "iScreen", "PIN copiato negli appunti", ToolTipIcon.None);
        };
        _autoStart.Click += (_, _) =>
        {
            StartsWithWindows = !StartsWithWindows;
            _autoStart.Checked = StartsWithWindows;
        };
        _menu.Opening += (_, _) => RefreshScreens();

        var admin = _fromService
            ? new ToolStripMenuItem("Disattiva il controllo delle app da amministratore…", null, (_, _) => DisableAdminMode())
            : new ToolStripMenuItem("Controlla anche le app da amministratore…", null, (_, _) => EnableAdminMode());
        var log = new ToolStripMenuItem("Apri il log", null, (_, _) => OpenLog());
        var quit = new ToolStripMenuItem("Esci da iScreen", null, (_, _) => Quit());
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _status, _pin, new ToolStripSeparator(), _screens, _autoStart, admin, log, new ToolStripSeparator(), quit,
        });
        RefreshScreens();
    }

    void RefreshScreens()
    {
        _monitors = Monitors.List();
        _screens.DropDownItems.Clear();
        foreach (var m in _monitors)
        {
            int index = m.Index;
            var item = new ToolStripMenuItem(Label(m, details: true))
            {
                Checked = index == _server.MonitorIndex,
                Enabled = !m.IsRotated,
            };
            item.Click += (_, _) => SelectScreen(index);
            _screens.DropDownItems.Add(item);
        }
    }

    void SelectScreen(int index)
    {
        var m = _monitors[index];
        _config.Monitor = m.FriendlyName;
        _config.Save();
        // Restarting capture takes a moment; keep the menu responsive.
        ThreadPool.QueueUserWorkItem(_ => _server.SetMonitor(index));
        UpdateStatus();
    }

    void OnClientChanged(string? address)
    {
        if (_quit) return; // the disconnect that Quit itself caused: the icon is already gone
        _client = address;
        _icon.Icon = address is null ? _idleIcon : _connectedIcon;
        _lastFrames = 0;
        _lastBytes = _server.Sender.BytesSent;
        if (address is not null)
            _icon.ShowBalloonTip(3000, "iScreen", $"Mac collegato ({address})", ToolTipIcon.Info);
        UpdateStatus();
    }

    void UpdateStatus()
    {
        var screen = _monitors.ElementAtOrDefault(_server.MonitorIndex);
        string screenName = screen is null ? "?" : Label(screen, details: false);
        string tooltip;
        if (_client is null)
        {
            _status.Text = $"In attesa del Mac · {screenName}";
            tooltip = $"iScreen · in attesa · PIN {_config.Pin}";
        }
        else
        {
            long frames = _server.Session?.FramesSent ?? 0, bytes = _server.Sender.BytesSent;
            double fps = Math.Max(0, frames - _lastFrames) / (_timer.Interval / 1000.0);
            double mbps = Math.Max(0, bytes - _lastBytes) * 8 / (_timer.Interval * 1000.0);
            (_lastFrames, _lastBytes) = (frames, bytes);
            _status.Text = $"Collegato: {_client} · {screenName} · {fps:0} fps · {mbps:0} Mbps";
            tooltip = $"iScreen · collegato a {_client}";
            LogStats(fps, mbps);
        }
        _pin.Text = $"PIN {_config.Pin}   (clic per copiare)";
        _icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
    }

    int _statsTicks;
    long _lastKeyframes, _lastResent, _lastRefused, _lastEncodeUs, _lastStatFrames;

    /// <summary>Every 10 s while connected: what the host is sending, for diagnosing the client side.</summary>
    void LogStats(double fps, double mbps)
    {
        if (++_statsTicks % 5 != 0 || _server.Session is not { } s) return;
        // Session counters restart with each session (reconnect, monitor switch): never go negative.
        static long Delta(long now, ref long last)
        {
            long d = now >= last ? now - last : now;
            last = now;
            return d;
        }
        long frames = Math.Max(1, Delta(s.FramesSent, ref _lastStatFrames));
        long encodeUs = Delta(s.EncodeUsTotal, ref _lastEncodeUs);
        long keyframes = Delta(s.Keyframes, ref _lastKeyframes);
        long resent = Delta(_server.Sender.PacketsResent, ref _lastResent);
        long refused = Delta(_server.Sender.ResendsRefused, ref _lastRefused);
        Console.WriteLine($"  sent {fps:0} fps, {mbps:0.0} Mbps, encode {encodeUs / 1000.0 / frames:0.0} ms, " +
                          $"keyframes +{keyframes}, resent packets +{resent}" + (refused > 0 ? $" (refused {refused})" : ""));
    }

    static string Label(MonitorInfo m, bool details)
    {
        string name = $"{m.FriendlyName} · {m.Width}×{m.Height}";
        if (!details) return name;
        if (m.IsPrimary) name += " (principale)";
        if (m.IsRotated) name += " — verticale, non ancora supportato";
        return name;
    }

    void Quit()
    {
        if (_quit) return;
        _quit = true;
        _power.Dispose();
        _timer.Stop();
        _cts.Cancel();
        _server.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        Console.WriteLine(RestartRequested ? "--- iScreen stopped, restarting ---" : "--- iScreen stopped ---");
        ExitThread();
    }
}
