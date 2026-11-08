using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using HidSharp;

namespace HMHubMonitor;

internal static class Program
{
    private static Mutex? _appMutex;

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--generate-previews")
        {
            string targetDir = args.Length > 1 ? args[1] : AppDomain.CurrentDomain.BaseDirectory;
            MonitorApplicationContext.GeneratePreviewImages(targetDir);
            return;
        }

        string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup_log.txt");


        try
        {
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [PID {Environment.ProcessId}] HM Lab Z-Neo Monitor starting (Admin: {IsAdministrator()})...\n");

            _appMutex = new Mutex(false, "Global\\HMHubMonitor_App_Mutex_Furkan");
            bool hasHandle = false;
            try
            {
                hasHandle = _appMutex.WaitOne(800, false);
            }
            catch (AbandonedMutexException)
            {
                hasHandle = true;
            }

            if (!hasHandle)
            {
                File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] [PID {Environment.ProcessId}] Application already running. Second instance terminated.\n");
                return;
            }

            ApplicationConfiguration.Initialize();
            var ctx = new MonitorApplicationContext(logPath);
            Application.Run(ctx);
        }
        catch (Exception ex)
        {
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] [PID {Environment.ProcessId}] ERROR: {ex}\n");
        }
        finally
        {
            try { _appMutex?.ReleaseMutex(); } catch { }
            _appMutex?.Dispose();
        }
    }
}

public enum MonitorDisplayMode
{
    CarouselAuto = 0,
    ClockOnly = 1,
    TempOnly = 2,
    WattOnly = 3,
    LoadOnly = 4,
    SciFiOrb = 5
}

internal class MonitorApplicationContext : ApplicationContext
{
    private readonly string _logPath;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly ToolStripMenuItem _styleSquareItem;
    private readonly ToolStripMenuItem _styleCircleItem;
    private readonly ToolStripMenuItem _styleFadedItem;

    private readonly ToolStripMenuItem _modeCarouselItem;
    private readonly ToolStripMenuItem _modeClockItem;
    private readonly ToolStripMenuItem _modeTempItem;
    private readonly ToolStripMenuItem _modeWattItem;
    private readonly ToolStripMenuItem _modeLoadItem;
    private readonly ToolStripMenuItem _modeOrbItem;

    private readonly ToolStripMenuItem _sec3Item;
    private readonly ToolStripMenuItem _sec5Item;
    private readonly ToolStripMenuItem _sec8Item;

    private readonly CancellationTokenSource _cts = new();

    private readonly object _rgbLock = new();
    private readonly byte[] _latestRgbBuffer = new byte[32 * 17 * 3];

    private HidStream? _hidStream;
    private bool _running = true;
    private int _currentStyle = 1;
    private MonitorDisplayMode _displayMode = MonitorDisplayMode.CarouselAuto;
    private int _pageIntervalMs = 5000;

    private static readonly uint[] CrcTable = new uint[256];

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(
        out System.Runtime.InteropServices.ComTypes.FILETIME idleTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernelTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME userTime);

    private static ulong _prevIdleTime = 0;
    private static ulong _prevKernelTime = 0;
    private static ulong _prevUserTime = 0;
    private static int _lastCalculatedCpuLoad = 0;
    private static float _smoothedCpuLoad = 0;
    private static float _smoothedCpuWatt = 25.0f;
    private static float _smoothedCpuTemp = 42.0f;
    private static float _smoothedGpuTemp = 38.0f;
    private static long _lastCpuSampleTick = 0;

    static MonitorApplicationContext()
    {
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int j = 0; j < 8; j++)
            {
                c = (c & 1) != 0 ? (0xEDB88320 ^ (c >> 1)) : (c >> 1);
            }
            CrcTable[i] = c;
        }

        try
        {
            if (GetSystemTimes(out var idle, out var kernel, out var user))
            {
                _prevIdleTime = FileTimeToUInt64(idle);
                _prevKernelTime = FileTimeToUInt64(kernel);
                _prevUserTime = FileTimeToUInt64(user);
                _lastCpuSampleTick = Environment.TickCount64;
            }
        }
        catch { }
    }

    private static ulong FileTimeToUInt64(System.Runtime.InteropServices.ComTypes.FILETIME ft)
    {
        return ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
    }

    private static int GetCpuUsage()
    {
        long now = Environment.TickCount64;
        if (now - _lastCpuSampleTick < 500 && _lastCpuSampleTick != 0)
        {
            return _lastCalculatedCpuLoad;
        }

        try
        {
            if (GetSystemTimes(out var idle, out var kernel, out var user))
            {
                ulong currIdle = FileTimeToUInt64(idle);
                ulong currKernel = FileTimeToUInt64(kernel);
                ulong currUser = FileTimeToUInt64(user);

                if (_prevKernelTime != 0 && _prevUserTime != 0)
                {
                    ulong idleDiff = currIdle - _prevIdleTime;
                    ulong kernelDiff = currKernel - _prevKernelTime;
                    ulong userDiff = currUser - _prevUserTime;
                    ulong totalDiff = kernelDiff + userDiff;

                    if (totalDiff > 0)
                    {
                        double usage = (1.0 - (double)idleDiff / totalDiff) * 100.0;
                        int rawLoad = Math.Clamp((int)Math.Round(usage), 0, 100);

                        if (_smoothedCpuLoad <= 0.01f) _smoothedCpuLoad = rawLoad;
                        else _smoothedCpuLoad = _smoothedCpuLoad * 0.70f + rawLoad * 0.30f;

                        _lastCalculatedCpuLoad = Math.Clamp((int)Math.Round(_smoothedCpuLoad), 0, 100);
                    }
                }

                _prevIdleTime = currIdle;
                _prevKernelTime = currKernel;
                _prevUserTime = currUser;
                _lastCpuSampleTick = now;
            }
        }
        catch { }
        return _lastCalculatedCpuLoad;
    }

    private static uint Crc32(byte[] data, int offset, int len)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < len; i++)
        {
            crc = (crc >> 8) ^ CrcTable[(crc ^ data[offset + i]) & 0xFF];
        }
        return ~crc;
    }

    private class DummyForm : Form
    {
        public DummyForm()
        {
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            FormBorderStyle = FormBorderStyle.None;
            Opacity = 0;
            Size = new Size(0, 0);
        }
        protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);
    }

    public MonitorApplicationContext(string logPath)
    {
        _logPath = logPath;
        MainForm = new DummyForm();
        var contextMenu = new ContextMenuStrip();

        var titleItem = new ToolStripMenuItem("HM Lab Z-Neo Monitor")
        {
            Enabled = false,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold)
        };
        contextMenu.Items.Add(titleItem);

        _statusItem = new ToolStripMenuItem("Status: Starting...")
        {
            Enabled = false
        };
        contextMenu.Items.Add(_statusItem);

        contextMenu.Items.Add(new ToolStripSeparator());

        var modeMenu = new ToolStripMenuItem("Display Mode");
        _modeCarouselItem = new ToolStripMenuItem("Auto Carousel (Clock -> Temp -> Power -> Load)", null, (s, e) => ChangeMode(MonitorDisplayMode.CarouselAuto)) { Checked = true };
        _modeClockItem = new ToolStripMenuItem("Clock Only (Large Digital Clock)", null, (s, e) => ChangeMode(MonitorDisplayMode.ClockOnly));
        _modeTempItem = new ToolStripMenuItem("Temperature Page (CPU & GPU \u00B0C)", null, (s, e) => ChangeMode(MonitorDisplayMode.TempOnly));
        _modeWattItem = new ToolStripMenuItem("Power Page (CPU & GPU Watt)", null, (s, e) => ChangeMode(MonitorDisplayMode.WattOnly));
        _modeLoadItem = new ToolStripMenuItem("Utilization Page (CPU & GPU %)", null, (s, e) => ChangeMode(MonitorDisplayMode.LoadOnly));
        _modeOrbItem = new ToolStripMenuItem("Cockpit Orb Mode (All-in-One HUD)", null, (s, e) => ChangeMode(MonitorDisplayMode.SciFiOrb));

        modeMenu.DropDownItems.Add(_modeCarouselItem);
        modeMenu.DropDownItems.Add(new ToolStripSeparator());
        modeMenu.DropDownItems.Add(_modeClockItem);
        modeMenu.DropDownItems.Add(_modeTempItem);
        modeMenu.DropDownItems.Add(_modeWattItem);
        modeMenu.DropDownItems.Add(_modeLoadItem);
        modeMenu.DropDownItems.Add(new ToolStripSeparator());
        modeMenu.DropDownItems.Add(_modeOrbItem);
        contextMenu.Items.Add(modeMenu);

        var timerMenu = new ToolStripMenuItem("Carousel Interval");
        _sec3Item = new ToolStripMenuItem("3 Seconds", null, (s, e) => SetPageInterval(3000));
        _sec5Item = new ToolStripMenuItem("5 Seconds (Default)", null, (s, e) => SetPageInterval(5000)) { Checked = true };
        _sec8Item = new ToolStripMenuItem("8 Seconds", null, (s, e) => SetPageInterval(8000));
        timerMenu.DropDownItems.Add(_sec3Item);
        timerMenu.DropDownItems.Add(_sec5Item);
        timerMenu.DropDownItems.Add(_sec8Item);
        contextMenu.Items.Add(timerMenu);

        var styleMenu = new ToolStripMenuItem("LED Matrix Style");
        _styleFadedItem = new ToolStripMenuItem("Smooth / Anti-Aliased (Faded)", null, (s, e) => ChangeStyle(1)) { Checked = true };
        _styleSquareItem = new ToolStripMenuItem("Square Pixel (Retro)", null, (s, e) => ChangeStyle(0));
        _styleCircleItem = new ToolStripMenuItem("Circle Matrix (Round)", null, (s, e) => ChangeStyle(2));
        styleMenu.DropDownItems.Add(_styleFadedItem);
        styleMenu.DropDownItems.Add(_styleSquareItem);
        styleMenu.DropDownItems.Add(_styleCircleItem);
        contextMenu.Items.Add(styleMenu);

        contextMenu.Items.Add(new ToolStripSeparator());

        _autoStartItem = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = IsAutoStartEnabled()
        };
        _autoStartItem.Click += (s, e) => ToggleAutoStart(_autoStartItem.Checked);
        contextMenu.Items.Add(_autoStartItem);

        contextMenu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit", null, (s, e) => ExitApp());
        contextMenu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = CreateTrayIcon(),
            ContextMenuStrip = contextMenu,
            Text = "HM Lab Z-Neo Monitor",
            Visible = true
        };

        DrawScreen(_latestRgbBuffer, 0, 38, 45, 71, 30, 65, 71, _displayMode);

        Task.Run(RunMainLoop);
    }

    private void SetPageInterval(int ms)
    {
        _pageIntervalMs = ms;
        _sec3Item.Checked = (ms == 3000);
        _sec5Item.Checked = (ms == 5000);
        _sec8Item.Checked = (ms == 8000);
    }

    private void ChangeMode(MonitorDisplayMode mode)
    {
        _displayMode = mode;
        _modeCarouselItem.Checked = (mode == MonitorDisplayMode.CarouselAuto);
        _modeClockItem.Checked = (mode == MonitorDisplayMode.ClockOnly);
        _modeTempItem.Checked = (mode == MonitorDisplayMode.TempOnly);
        _modeWattItem.Checked = (mode == MonitorDisplayMode.WattOnly);
        _modeLoadItem.Checked = (mode == MonitorDisplayMode.LoadOnly);
        _modeOrbItem.Checked = (mode == MonitorDisplayMode.SciFiOrb);
    }

    private void UpdateStyleMenuChecks(int style)
    {
        _currentStyle = style;
        _styleSquareItem.Checked = (style == 0);
        _styleFadedItem.Checked = (style == 1);
        _styleCircleItem.Checked = (style == 2);
    }

    private void ChangeStyle(int style)
    {
        UpdateStyleMenuChecks(style);
        Task.Run(() => SetHubConfig(srgbMode: true, style: style));
    }

    private static Icon CreateTrayIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.FromArgb(160, 40, 240));
            g.FillRectangle(brush, 2, 2, 12, 12);
            using var innerBrush = new SolidBrush(Color.FromArgb(15, 20, 30));
            g.FillRectangle(innerBrush, 4, 4, 8, 8);
            using var ledBrush = new SolidBrush(Color.FromArgb(0, 220, 255));
            g.FillRectangle(ledBrush, 6, 6, 4, 4);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private void Log(string msg)
    {
        try
        {
            File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\n");
        }
        catch { }
    }

    private void UpdateStatus(string status, string? tooltip = null)
    {
        try
        {
            _statusItem.Text = $"Status: {status}";
            if (!string.IsNullOrEmpty(tooltip))
            {
                _notifyIcon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
            }
        }
        catch { }
    }

    private async Task RunMainLoop()
    {
        Log("Starting HM Lab Monitor 5s carousel mode...");
        EnsureHubConfig();

        var sensorTask = Task.Run(SensorLoop);
        var streamTask = Task.Run(StreamLoop);

        await Task.WhenAll(sensorTask, streamTask);
    }

    private async Task SensorLoop()
    {
        byte[] localRgb = new byte[32 * 17 * 3];
        int activePageIndex = 0;
        var pageStopwatch = Stopwatch.StartNew();

        while (_running && !_cts.IsCancellationRequested)
        {
            if (File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HMHubMonitor", "exit.signal")))
            {
                try { File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HMHubMonitor", "exit.signal")); } catch { }
                _running = false;
                _cts.Cancel();
                Application.Exit();
                break;
            }

            try
            {
                int cpuTemp = 0;
                int cpuMhz = GetCpuCurrentMhz();
                int cpuLoad = GetCpuUsage();
                int cpuWatt = 0;

                var nv = GetNvidiaMetrics();
                int gpuTemp = nv.Temp;
                int gpuMhz = nv.Mhz;
                int gpuWatt = nv.Watt;
                int gpuLoad = nv.Load;

                var hwInfo = GetHwInfoMetrics();
                if (hwInfo.CpuTemp > 0) cpuTemp = hwInfo.CpuTemp;
                if (hwInfo.CpuWatt > 0) cpuWatt = hwInfo.CpuWatt;
                if (gpuTemp == 0 && hwInfo.GpuTemp > 0) gpuTemp = hwInfo.GpuTemp;
                if (gpuWatt == 0 && hwInfo.GpuWatt > 0) gpuWatt = hwInfo.GpuWatt;
                if (gpuLoad == 0 && hwInfo.GpuLoad > 0) gpuLoad = hwInfo.GpuLoad;

                if (cpuWatt == 0)
                {
                    float freqBoostW = Math.Max(0, (cpuMhz - 3500) / 1500.0f) * 15.0f;
                    float targetWatt = 22.0f + (cpuLoad / 100.0f) * 75.0f + freqBoostW;
                    _smoothedCpuWatt = _smoothedCpuWatt * 0.85f + targetWatt * 0.15f;
                    cpuWatt = (int)Math.Round(_smoothedCpuWatt);
                }
                else
                {
                    _smoothedCpuWatt = cpuWatt;
                }

                if (cpuTemp == 0)
                {
                    float ambientBase = Math.Max(28.0f, (gpuTemp > 0 ? gpuTemp - 2.0f : 32.0f));
                    float freqBoostT = Math.Max(0, (cpuMhz - 3500) / 1500.0f) * 12.0f;
                    float targetTemp = ambientBase + 10.0f + (cpuLoad / 100.0f) * 32.0f + freqBoostT;

                    _smoothedCpuTemp = _smoothedCpuTemp * 0.90f + targetTemp * 0.10f;
                    cpuTemp = (int)Math.Round(_smoothedCpuTemp);
                }
                else
                {
                    _smoothedCpuTemp = cpuTemp;
                }

                if (gpuTemp == 0)
                {
                    float targetGpuTemp = 36.0f + (gpuLoad / 100.0f) * 35.0f;
                    _smoothedGpuTemp = _smoothedGpuTemp * 0.92f + targetGpuTemp * 0.08f;
                    gpuTemp = (int)Math.Round(_smoothedGpuTemp);
                }
                else
                {
                    _smoothedGpuTemp = gpuTemp;
                }

                if (cpuMhz <= 0) cpuMhz = 3391;
                if (gpuMhz <= 0) gpuMhz = 299;

                if (pageStopwatch.ElapsedMilliseconds >= _pageIntervalMs)
                {
                    activePageIndex = (activePageIndex + 1) % 4;
                    pageStopwatch.Restart();
                }

                string pageName = activePageIndex switch
                {
                    0 => "Clock",
                    1 => "Temperature (\u00B0C)",
                    2 => "Power (Watt)",
                    _ => "Utilization (%)"
                };

                string cpuSummary = $"CPU: {cpuTemp}\u00B0C {cpuWatt}W {cpuLoad}%";
                string gpuSummary = $"GPU: {gpuTemp}\u00B0C {gpuWatt}W {gpuLoad}%";
                UpdateStatus($"[{pageName}] {cpuSummary} | {gpuSummary}", $"Z-Neo [{pageName}] | {cpuSummary} | {gpuSummary}");

                DrawScreen(localRgb, activePageIndex, cpuTemp, cpuWatt, cpuLoad, gpuTemp, gpuWatt, gpuLoad, _displayMode);

                lock (_rgbLock)
                {
                    Buffer.BlockCopy(localRgb, 0, _latestRgbBuffer, 0, localRgb.Length);
                }

                await Task.Delay(100, _cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log($"Sensor loop error: {ex.Message}");
                await Task.Delay(1000, _cts.Token);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorPowerInformation
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    [DllImport("powrprof.dll")]
    private static extern int CallNtPowerInformation(
        int informationLevel,
        IntPtr lpInputBuffer,
        int nInputBufferSize,
        [Out] ProcessorPowerInformation[] lpOutputBuffer,
        int nOutputBufferSize);

    private static int _cachedCpuMhz = 4500;
    private static long _lastMhzTick = 0;

    private static int GetCpuCurrentMhz()
    {
        long now = Environment.TickCount64;
        if (now - _lastMhzTick < 500) return _cachedCpuMhz;
        _lastMhzTick = now;

        try
        {
            int count = Environment.ProcessorCount;
            var info = new ProcessorPowerInformation[count];
            int size = Marshal.SizeOf(typeof(ProcessorPowerInformation)) * count;
            int status = CallNtPowerInformation(11, IntPtr.Zero, 0, info, size);
            if (status == 0)
            {
                uint maxActive = 0;
                for (int i = 0; i < count; i++)
                {
                    if (info[i].CurrentMhz > maxActive) maxActive = info[i].CurrentMhz;
                }
                if (maxActive > 500)
                {
                    _cachedCpuMhz = (int)maxActive;
                    return _cachedCpuMhz;
                }
            }
        }
        catch { }

        return _cachedCpuMhz;
    }

    private static long _lastHwInfoCheckTick = 0;
    private static bool _hwInfoAvailable = false;

    private static (int CpuTemp, int CpuWatt, int GpuTemp, int GpuWatt, int GpuLoad) GetHwInfoMetrics()
    {
        long now = Environment.TickCount64;
        if (!_hwInfoAvailable && (now - _lastHwInfoCheckTick < 3000))
        {
            return (0, 0, 0, 0, 0);
        }
        _lastHwInfoCheckTick = now;

        try
        {
            using var mmf = MemoryMappedFile.OpenExisting("Global\\HWiNFO_SENS_SM2", MemoryMappedFileRights.Read);
            using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            uint sig = accessor.ReadUInt32(0);
            if (sig != 0x53695748)
            {
                _hwInfoAvailable = false;
                return (0, 0, 0, 0, 0);
            }

            uint offsetReadings = accessor.ReadUInt32(32);
            uint sizeReading = accessor.ReadUInt32(36);
            uint numReadings = accessor.ReadUInt32(40);

            if (sizeReading < 128 || numReadings == 0 || numReadings > 2000)
            {
                _hwInfoAvailable = false;
                return (0, 0, 0, 0, 0);
            }

            _hwInfoAvailable = true;
            int cpuTemp = 0;
            int cpuWatt = 0;
            int gpuTemp = 0;
            int gpuWatt = 0;
            int gpuLoad = 0;
            byte[] labelBuffer = new byte[128];

            for (int i = 0; i < numReadings; i++)
            {
                long elementOffset = offsetReadings + ((long)i * sizeReading);
                uint type = accessor.ReadUInt32(elementOffset);
                accessor.ReadArray(elementOffset + 12, labelBuffer, 0, 128);
                string label = System.Text.Encoding.Default.GetString(labelBuffer).TrimEnd('\0');

                if (type == 6 && cpuTemp == 0)
                {
                    if (label.Contains("CPU (Tctl/Tdie)") || label.Contains("CPU Die") || label.Contains("CPU Core") || label.Contains("Tctl"))
                    {
                        double val = accessor.ReadDouble(elementOffset + 284);
                        if (val > 15 && val < 115) cpuTemp = (int)Math.Round(val);
                    }
                }
                else if (type == 3 && cpuWatt == 0)
                {
                    if (label.Contains("CPU Package Power") || label.Contains("Package Power") || label.Contains("Core Power"))
                    {
                        double val = accessor.ReadDouble(elementOffset + 284);
                        if (val > 0 && val < 350) cpuWatt = (int)Math.Round(val);
                    }
                }
                else if (type == 6 && gpuTemp == 0 && (label.Contains("GPU Temperature") || label.Contains("GPU Core Temp")))
                {
                    double val = accessor.ReadDouble(elementOffset + 284);
                    if (val > 15 && val < 115) gpuTemp = (int)Math.Round(val);
                }
                else if (type == 3 && gpuWatt == 0 && (label.Contains("GPU Power") || label.Contains("Total Board Power") || label.Contains("GPU Package Power")))
                {
                    double val = accessor.ReadDouble(elementOffset + 284);
                    if (val > 0 && val < 600) gpuWatt = (int)Math.Round(val);
                }
                else if (type == 2 && gpuLoad == 0 && (label.Contains("GPU Core Load") || label.Contains("GPU D3D Usage") || label.Contains("GPU Utilization")))
                {
                    double val = accessor.ReadDouble(elementOffset + 284);
                    if (val >= 0 && val <= 100) gpuLoad = (int)Math.Round(val);
                }
            }

            return (cpuTemp, cpuWatt, gpuTemp, gpuWatt, gpuLoad);
        }
        catch
        {
            _hwInfoAvailable = false;
        }
        return (0, 0, 0, 0, 0);
    }

    private static (int Temp, int Mhz, int Watt, int Load) _cachedNvidia = (34, 622, 13, 0);
    private static long _lastNvidiaTick = 0;

    private static (int Temp, int Mhz, int Watt, int Load) GetNvidiaMetrics()
    {
        long now = Environment.TickCount64;
        if (now - _lastNvidiaTick < 800)
        {
            return _cachedNvidia;
        }
        _lastNvidiaTick = now;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=temperature.gpu,clocks.current.graphics,power.draw,utilization.gpu --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                string output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(400);
                var parts = output.Split(',');
                if (parts.Length >= 4)
                {
                    double.TryParse(parts[0].Trim().Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double tempD);
                    double.TryParse(parts[1].Trim().Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double mhzD);
                    double.TryParse(parts[2].Trim().Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double wattD);
                    double.TryParse(parts[3].Trim().Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double loadD);
                    _cachedNvidia = ((int)Math.Round(tempD), (int)Math.Round(mhzD), (int)Math.Round(wattD), (int)Math.Round(loadD));
                    return _cachedNvidia;
                }
            }
        }
        catch { }
        return _cachedNvidia;
    }

    private async Task StreamLoop()
    {
        byte[] frameBuffer = new byte[32 * 17 * 3];
        const int payloadLen = 252;
        int pkgCount = (int)Math.Ceiling(frameBuffer.Length / (double)payloadLen);
        int lastSecond = -1;

        while (_running && !_cts.IsCancellationRequested)
        {
            if (File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HMHubMonitor", "exit.signal")))
            {
                try { File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HMHubMonitor", "exit.signal")); } catch { }
                _running = false;
                _cts.Cancel();
                Application.Exit();
                break;
            }

            try
            {
                if (_hidStream == null)
                {
                    _hidStream = ConnectHid();
                    if (_hidStream == null)
                    {
                        UpdateStatus("Searching for Hub...");
                        await Task.Delay(1000, _cts.Token);
                        continue;
                    }
                    Log("HID COL05 stream connected. Live 30 FPS active.");
                }

                lock (_rgbLock)
                {
                    Buffer.BlockCopy(_latestRgbBuffer, 0, frameBuffer, 0, frameBuffer.Length);
                }

                for (int i = 0; i < pkgCount; i++)
                {
                    byte[] packet = new byte[257];
                    packet[0] = 0x05;
                    packet[1] = 1;
                    packet[2] = (byte)pkgCount;
                    packet[3] = (byte)i;

                    int offset = i * payloadLen;
                    int count = Math.Min(payloadLen, frameBuffer.Length - offset);
                    Buffer.BlockCopy(frameBuffer, offset, packet, 4, count);

                    _hidStream.Write(packet);
                }

                var now = DateTime.Now;
                if (now.Second != lastSecond)
                {
                    lastSecond = now.Second;
                    byte[] timePacket = new byte[257];
                    timePacket[0] = 0x05;
                    timePacket[1] = 2;
                    timePacket[2] = (byte)(now.Year & 0xFF);
                    timePacket[3] = (byte)(now.Year >> 8);
                    timePacket[4] = (byte)now.Month;
                    timePacket[5] = (byte)(now.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)now.DayOfWeek);
                    timePacket[6] = (byte)now.Hour;
                    timePacket[7] = (byte)now.Minute;
                    timePacket[8] = (byte)now.Second;
                    _hidStream.Write(timePacket);
                }

                await Task.Delay(33, _cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log($"HID stream error: {ex.Message}");
                try { _hidStream?.Dispose(); } catch { }
                _hidStream = null;
                UpdateStatus("Reconnecting...");
                await Task.Delay(1000, _cts.Token);
            }
        }
    }

    private static HidStream? ConnectHid()
    {
        try
        {
            var devices = DeviceList.Local.GetHidDevices(0xC019, 0x0401).ToList();
            var col05 = devices.FirstOrDefault(d => d.DevicePath.ToLower().Contains("col05"));
            if (col05 != null && col05.TryOpen(out var stream))
            {
                return stream;
            }
        }
        catch { }
        return null;
    }

    private static string? _detectedComPort = null;

    private static IEnumerable<string> GetCandidateComPorts()
    {
        var list = new List<string>();
        if (!string.IsNullOrEmpty(_detectedComPort)) list.Add(_detectedComPort);
        list.Add("COM3");
        try
        {
            foreach (var p in SerialPort.GetPortNames())
            {
                if (!list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p);
            }
        }
        catch { }
        return list;
    }

    private void EnsureHubConfig()
    {
        try
        {
            Log("Detecting Hub serial port and checking sRGB configuration...");
            SetHubConfig(srgbMode: true, style: _currentStyle);
        }
        catch (Exception ex)
        {
            Log($"Hub configuration check skipped: {ex.Message}");
        }
    }

    private void SetHubConfig(bool srgbMode, int style)
    {
        foreach (var portName in GetCandidateComPorts())
        {
            try
            {
                using var port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
                {
                    DtrEnable = true,
                    RtsEnable = true,
                    ReadTimeout = 400,
                    WriteTimeout = 400
                };
                port.Open();

                byte[] req = new byte[256];
                req[0] = 1;
                uint crc = Crc32(req, 0, 252);
                req[252] = (byte)(crc >> 0);
                req[253] = (byte)(crc >> 8);
                req[254] = (byte)(crc >> 16);
                req[255] = (byte)(crc >> 24);
                port.Write(req, 0, 256);

                byte[] buf = new byte[256];
                bool gotReply = false;
                for (int i = 0; i < 5; i++)
                {
                    ReadExact(port, buf);
                    if (buf[0] == 1) { gotReply = true; break; }
                }
                if (!gotReply) continue;

                byte[] cfgReq = new byte[256];
                cfgReq[0] = 3;
                cfgReq[1] = 1;
                uint cfgCrc = Crc32(cfgReq, 0, 252);
                cfgReq[252] = (byte)(cfgCrc >> 0);
                cfgReq[253] = (byte)(cfgCrc >> 8);
                cfgReq[254] = (byte)(cfgCrc >> 16);
                cfgReq[255] = (byte)(cfgCrc >> 24);
                port.Write(cfgReq, 0, 256);

                byte[] config24 = new byte[24];
                bool gotConfig = false;
                for (int i = 0; i < 15; i++)
                {
                    ReadExact(port, buf);
                    if (buf[0] == 3 && buf[1] == 2)
                    {
                        Buffer.BlockCopy(buf, 6, config24, 0, 24);
                        gotConfig = true;
                        break;
                    }
                }

                if (gotConfig)
                {
                    _detectedComPort = portName;
                    bool needUpdate = false;
                    if (srgbMode && config24[2] != 3)
                    {
                        config24[2] = 3;
                        needUpdate = true;
                    }
                    if (config24[23] != style)
                    {
                        config24[23] = (byte)style;
                        needUpdate = true;
                    }

                    if (needUpdate)
                    {
                        byte[] dataWithCrc = new byte[28];
                        Buffer.BlockCopy(config24, 0, dataWithCrc, 0, 24);
                        uint dataCrc = Crc32(config24, 0, 24);
                        dataWithCrc[24] = (byte)(dataCrc >> 0);
                        dataWithCrc[25] = (byte)(dataCrc >> 8);
                        dataWithCrc[26] = (byte)(dataCrc >> 16);
                        dataWithCrc[27] = (byte)(dataCrc >> 24);

                        byte[] utPacket = new byte[256];
                        utPacket[0] = 3;
                        utPacket[1] = 2;
                        utPacket[2] = 1;
                        utPacket[3] = 0;
                        utPacket[4] = 0;
                        utPacket[5] = 0;
                        utPacket[6] = 28;
                        utPacket[7] = 0;
                        Buffer.BlockCopy(dataWithCrc, 0, utPacket, 8, 28);

                        uint pktCrc = Crc32(utPacket, 0, 252);
                        utPacket[252] = (byte)(pktCrc >> 0);
                        utPacket[253] = (byte)(pktCrc >> 8);
                        utPacket[254] = (byte)(pktCrc >> 16);
                        utPacket[255] = (byte)(pktCrc >> 24);

                        port.Write(utPacket, 0, 256);
                        Log($"Hub configured on {portName}: Mode={config24[2]}, Style={style}");
                    }
                    break;
                }
            }
            catch { }
        }
    }

    private static void ReadExact(SerialPort port, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            read += port.Read(buffer, read, buffer.Length - read);
        }
    }

    private static void SetPixel(byte[] rgb, int x, int y, byte r, byte g, byte b, float alpha = 1.0f)
    {
        if (x < 0 || x >= 32 || y < 0 || y >= 17 || alpha <= 0.01f) return;
        int idx = (y * 32 + x) * 3;
        float inv = 1.0f - alpha;
        rgb[idx + 0] = (byte)Math.Clamp((int)Math.Round(rgb[idx + 0] * inv + r * alpha), 0, 255);
        rgb[idx + 1] = (byte)Math.Clamp((int)Math.Round(rgb[idx + 1] * inv + g * alpha), 0, 255);
        rgb[idx + 2] = (byte)Math.Clamp((int)Math.Round(rgb[idx + 2] * inv + b * alpha), 0, 255);
    }

    private static readonly Dictionary<char, byte[][]> CrispClockFont2px = new()
    {
        { '0', new byte[][] {
            new byte[] { 0, 1, 1, 1, 0 },
            new byte[] { 1, 1, 1, 1, 1 },
            new byte[] { 1, 1, 0, 1, 1 },
            new byte[] { 1, 1, 0, 1, 1 },
            new byte[] { 1, 1, 0, 1, 1 },
            new byte[] { 1, 1, 0, 1, 1 },
            new byte[] { 1, 1, 0, 1, 1 },
            new byte[] { 1, 1, 0, 1, 1 },
            new byte[] { 1, 1, 1, 1, 1 },
            new byte[] { 0, 1, 1, 1, 0 }
        } },
        { '1', new byte[][] {
            new byte[] { 0, 1, 1 },
            new byte[] { 1, 1, 1 },
            new byte[] { 0, 1, 1 },
            new byte[] { 0, 1, 1 },
            new byte[] { 0, 1, 1 },
            new byte[] { 0, 1, 1 },
            new byte[] { 0, 1, 1 },
            new byte[] { 0, 1, 1 },
            new byte[] { 0, 1, 1 },
            new byte[] { 0, 1, 1 }
        } },
        { '2', new byte[][] {
            new byte[] { 0, 1, 1, 1, 0 },
            new byte[] { 1, 1, 1, 1, 1 },
            new byte[] { 0, 0, 0, 1, 1 },
            new byte[] { 0, 0, 0, 1, 1 },
            new byte[] { 0, 0, 1, 1, 0 },
            new byte[] { 0, 0, 1, 1, 0 },
            new byte[] { 0, 1, 1, 0, 0 },
            new byte[] { 1, 1, 0, 0, 0 },
            new byte[] { 1, 1, 1, 1, 1 },
            new byte[] { 1, 1, 1, 1, 1 }
        } },
        { '3', new byte[][] {
            new byte[] { 0, 1, 1, 1, 0 },
            new byte[] { 1, 1, 1, 1, 1 },
            new byte[] { 0, 0, 0, 1, 1 },
            new byte[] { 0, 0, 0, 1, 1 },
            new byte[] { 0, 1, 1, 1, 1 },
            new byte[] { 0, 1, 1, 1, 1 },
            new byte[] { 0, 0, 0, 1, 1 },
            new byte[] { 0, 0, 0, 1, 1 },
            new byte[] { 1, 1, 1, 1, 1 },
            new byte[] { 0, 1, 1, 1, 0 }
        } },
        { '4', new byte[][] {
            new byte[] { 1, 1, 0, 1, 1 },
            new byte[] { 1, 1, 0, 1, 1 },
            new byte[] { 1, 1, 0, 1, 1 },
            new byte[] { 1, 1, 0, 1, 1 },
            new byte[] { 1, 1, 1, 1, 1 },
            new byte[] { 1, 1, 1, 1, 1 },
            new byte[] { 0, 0, 0, 1, 1 },
            new byte[] { 0, 0, 0, 1, 1 },
            new byte[] { 0, 0, 0, 1, 1 },
            new byte[] { 0, 0, 0, 1, 1 }
        } },
        { '5', new byte[][] {
            new byte[] { 1, 1, 1, 1, 1 },
            new byte[] { 1, 1, 1, 1, 1 },
            new byte[] { 1, 1, 0, 0, 0 },
            new byte[] { 1, 1, 0, 0, 0 },
            new byte[] { 1, 1, 1, 1, 0 },
            new byte[] { 1, 1, 1, 1, 1 },
            new byte[] { 0, 0, 0, 1, 1 },
            new byte[] { 0, 0, 0, 1, 1 },
            new byte[] { 1, 1, 1, 1, 1 },
            new byte[] { 0, 1, 1, 1, 0 }
        } },
        { '6', new byte[][] {
            new byte[] { 0, 1, 1, 1, 0 },
            new byte[] { 1, 1, 1, 1, 1 },
            new byte[] { 1, 1, 0, 0, 0 },
            new byte[] { 1, 1, 0, 0, 0 },
            new byte[] { 1, 1, 1, 1, 0 },
            new byte[] { 1, 1, 1, 1, 1 },
            new byte[] { 1, 1, 0, 1, 1 },
            new byte[] { 1, 1, 0, 1, 1 },
            new byte[] { 1, 1, 1, 1, 1 },
            new byte[] { 0, 1, 1, 1, 0 }
        } },
        { '7', new byte[][] {
            new byte[] { 1, 1, 1, 1, 1 },
            new byte[] { 1, 1, 1, 1, 1 },
            new byte[] { 0, 0, 0, 1, 1 },
            new byte[] { 0, 0, 0, 1, 1 },
            new byte[] { 0, 0, 1, 1, 0 },
            new byte[] { 0, 0, 1, 1, 0 },
            new byte[] { 0, 1, 1, 0, 0 },
            new byte[] { 0, 1, 1, 0, 0 },
            new byte[] { 1, 1, 0, 0, 0 },
            new byte[] { 1, 1, 0, 0, 0 }
        } },
        { '8', new byte[][] {
            new byte[] { 0, 1, 1, 1, 0 },
            new byte[] { 1, 1, 1, 1, 1 },
            new byte[] { 1, 1, 0, 1, 1 },
            new byte[] { 1, 1, 0, 1, 1 },
            new byte[] { 0, 1, 1, 1, 0 },
            new byte[] { 0, 1, 1, 1, 0 },
            new byte[] { 1, 1, 0, 1, 1 },
            new byte[] { 1, 1, 0, 1, 1 },
            new byte[] { 1, 1, 1, 1, 1 },
            new byte[] { 0, 1, 1, 1, 0 }
        } },
        { '9', new byte[][] {
            new byte[] { 0, 1, 1, 1, 0 },
            new byte[] { 1, 1, 1, 1, 1 },
            new byte[] { 1, 1, 0, 1, 1 },
            new byte[] { 1, 1, 0, 1, 1 },
            new byte[] { 1, 1, 1, 1, 1 },
            new byte[] { 0, 1, 1, 1, 1 },
            new byte[] { 0, 0, 0, 1, 1 },
            new byte[] { 0, 0, 0, 1, 1 },
            new byte[] { 1, 1, 1, 1, 1 },
            new byte[] { 0, 1, 1, 1, 0 }
        } },
        { ':', new byte[][] {
            new byte[] { 0, 0 },
            new byte[] { 0, 0 },
            new byte[] { 1, 1 },
            new byte[] { 1, 1 },
            new byte[] { 0, 0 },
            new byte[] { 0, 0 },
            new byte[] { 1, 1 },
            new byte[] { 1, 1 },
            new byte[] { 0, 0 },
            new byte[] { 0, 0 }
        } }
    };

    private static readonly Dictionary<char, byte[][]> CrispFont3x5 = new()
    {
        { '0', new byte[][] { new byte[] { 1, 1, 1 }, new byte[] { 1, 0, 1 }, new byte[] { 1, 0, 1 }, new byte[] { 1, 0, 1 }, new byte[] { 1, 1, 1 } } },
        { '1', new byte[][] { new byte[] { 0, 1 }, new byte[] { 1, 1 }, new byte[] { 0, 1 }, new byte[] { 0, 1 }, new byte[] { 1, 1 } } },
        { '2', new byte[][] { new byte[] { 1, 1, 1 }, new byte[] { 0, 0, 1 }, new byte[] { 1, 1, 1 }, new byte[] { 1, 0, 0 }, new byte[] { 1, 1, 1 } } },
        { '3', new byte[][] { new byte[] { 1, 1, 1 }, new byte[] { 0, 0, 1 }, new byte[] { 1, 1, 1 }, new byte[] { 0, 0, 1 }, new byte[] { 1, 1, 1 } } },
        { '4', new byte[][] { new byte[] { 1, 0, 1 }, new byte[] { 1, 0, 1 }, new byte[] { 1, 1, 1 }, new byte[] { 0, 0, 1 }, new byte[] { 0, 0, 1 } } },
        { '5', new byte[][] { new byte[] { 1, 1, 1 }, new byte[] { 1, 0, 0 }, new byte[] { 1, 1, 1 }, new byte[] { 0, 0, 1 }, new byte[] { 1, 1, 1 } } },
        { '6', new byte[][] { new byte[] { 1, 1, 1 }, new byte[] { 1, 0, 0 }, new byte[] { 1, 1, 1 }, new byte[] { 1, 0, 1 }, new byte[] { 1, 1, 1 } } },
        { '7', new byte[][] { new byte[] { 1, 1, 1 }, new byte[] { 0, 0, 1 }, new byte[] { 0, 1, 0 }, new byte[] { 0, 1, 0 }, new byte[] { 0, 1, 0 } } },
        { '8', new byte[][] { new byte[] { 1, 1, 1 }, new byte[] { 1, 0, 1 }, new byte[] { 1, 1, 1 }, new byte[] { 1, 0, 1 }, new byte[] { 1, 1, 1 } } },
        { '9', new byte[][] { new byte[] { 1, 1, 1 }, new byte[] { 1, 0, 1 }, new byte[] { 1, 1, 1 }, new byte[] { 0, 0, 1 }, new byte[] { 1, 1, 1 } } },
        { ':', new byte[][] { new byte[] { 0 }, new byte[] { 1 }, new byte[] { 0 }, new byte[] { 1 }, new byte[] { 0 } } },
        { 'C', new byte[][] { new byte[] { 1, 1, 1 }, new byte[] { 1, 0, 0 }, new byte[] { 1, 0, 0 }, new byte[] { 1, 0, 0 }, new byte[] { 1, 1, 1 } } },
        { 'P', new byte[][] { new byte[] { 1, 1, 1 }, new byte[] { 1, 0, 1 }, new byte[] { 1, 1, 1 }, new byte[] { 1, 0, 0 }, new byte[] { 1, 0, 0 } } },
        { 'U', new byte[][] { new byte[] { 1, 0, 1 }, new byte[] { 1, 0, 1 }, new byte[] { 1, 0, 1 }, new byte[] { 1, 0, 1 }, new byte[] { 1, 1, 1 } } },
        { 'G', new byte[][] { new byte[] { 1, 1, 1 }, new byte[] { 1, 0, 0 }, new byte[] { 1, 0, 1 }, new byte[] { 1, 0, 1 }, new byte[] { 1, 1, 1 } } },
        { 'W', new byte[][] { new byte[] { 1, 0, 1 }, new byte[] { 1, 0, 1 }, new byte[] { 1, 0, 1 }, new byte[] { 1, 1, 1 }, new byte[] { 1, 0, 1 } } },
        { '%', new byte[][] { new byte[] { 1, 0, 1 }, new byte[] { 0, 0, 1 }, new byte[] { 0, 1, 0 }, new byte[] { 1, 0, 0 }, new byte[] { 1, 0, 1 } } },
        { '-', new byte[][] { new byte[] { 0, 0 }, new byte[] { 0, 0 }, new byte[] { 1, 1 }, new byte[] { 0, 0 }, new byte[] { 0, 0 } } },
        { '\u00B0', new byte[][] { new byte[] { 1, 1 }, new byte[] { 1, 1 }, new byte[] { 0, 0 }, new byte[] { 0, 0 }, new byte[] { 0, 0 } } },
        { ' ', new byte[][] { new byte[] { 0 }, new byte[] { 0 }, new byte[] { 0 }, new byte[] { 0 }, new byte[] { 0 } } },
        { '.', new byte[][] { new byte[] { 0 }, new byte[] { 0 }, new byte[] { 0 }, new byte[] { 0 }, new byte[] { 1 } } },
        { 'M', new byte[][] { new byte[] { 1, 0, 1 }, new byte[] { 1, 1, 1 }, new byte[] { 1, 0, 1 }, new byte[] { 1, 0, 1 }, new byte[] { 1, 0, 1 } } },
        { 'H', new byte[][] { new byte[] { 1, 0, 1 }, new byte[] { 1, 0, 1 }, new byte[] { 1, 1, 1 }, new byte[] { 1, 0, 1 }, new byte[] { 1, 0, 1 } } },
        { 'z', new byte[][] { new byte[] { 0, 0, 0 }, new byte[] { 1, 1, 1 }, new byte[] { 0, 1, 0 }, new byte[] { 1, 0, 0 }, new byte[] { 1, 1, 1 } } }
    };

    private static int DrawCrispText(byte[] rgb, int startX, int startY, string text, byte r, byte g, byte b, int spacing = 1)
    {
        int curX = startX;
        foreach (char ch in text)
        {
            if (!CrispFont3x5.TryGetValue(ch, out var glyph))
            {
                curX += 2;
                continue;
            }
            for (int row = 0; row < glyph.Length; row++)
            {
                for (int col = 0; col < glyph[row].Length; col++)
                {
                    if (glyph[row][col] == 1)
                    {
                        SetPixel(rgb, curX + col, startY + row, r, g, b, 1.0f);
                    }
                }
            }
            curX += glyph[0].Length + spacing;
        }
        return curX;
    }

    private static int GetCrispTextWidth(string text, int spacing = 1)
    {
        int width = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            int w = CrispFont3x5.TryGetValue(ch, out var g) ? g[0].Length : 2;
            width += w + (i < text.Length - 1 ? spacing : 0);
        }
        return width;
    }

    private static void DrawRightAlignedText(byte[] rgb, int rightEdgeX, int startY, string text, byte r, byte g, byte b, int spacing = 1)
    {
        int w = GetCrispTextWidth(text, spacing);
        int startX = Math.Max(0, rightEdgeX - w + 1);
        DrawCrispText(rgb, startX, startY, text, r, g, b, spacing);
    }

    private static void DrawValueWithUnit(byte[] rgb, int rightEdgeX, int startY, string numStr, string unitStr, byte unitR, byte unitG, byte unitB)
    {
        int totalW = GetCrispTextWidth(numStr + unitStr);
        int startX = Math.Max(0, rightEdgeX - totalW + 1);
        int nextX = DrawCrispText(rgb, startX, startY, numStr, 255, 255, 255);
        DrawCrispText(rgb, nextX, startY, unitStr, unitR, unitG, unitB);
    }

    private static void DrawScreen(byte[] rgb, int carouselPage, int cpuTemp, int cpuWatt, int cpuLoad, int gpuTemp, int gpuWatt, int gpuLoad, MonitorDisplayMode mode)
    {
        Array.Clear(rgb, 0, rgb.Length);

        int pageToRender = carouselPage;
        if (mode == MonitorDisplayMode.ClockOnly) pageToRender = 0;
        else if (mode == MonitorDisplayMode.TempOnly) pageToRender = 1;
        else if (mode == MonitorDisplayMode.WattOnly) pageToRender = 2;
        else if (mode == MonitorDisplayMode.LoadOnly) pageToRender = 3;
        else if (mode == MonitorDisplayMode.SciFiOrb)
        {
            DrawScreenSciFiOrb(rgb, cpuTemp, cpuWatt, cpuLoad, gpuTemp, gpuWatt, gpuLoad);
            return;
        }

        switch (pageToRender)
        {
            case 0:
                DrawPage0_Clock(rgb);
                break;
            case 1:
                DrawPage1_Temp(rgb, cpuTemp, gpuTemp, cpuLoad, gpuLoad);
                break;
            case 2:
                DrawPage2_Watt(rgb, cpuWatt, gpuWatt, cpuLoad, gpuLoad);
                break;
            case 3:
                DrawPage3_Load(rgb, cpuLoad, gpuLoad);
                break;
        }
    }

    private static void DrawPage0_Clock(byte[] rgb, bool? forceColon = null)
    {
        Array.Clear(rgb, 0, rgb.Length);

        var now = DateTime.Now;
        string timeStr = now.ToString("HH:mm");
        bool colonVisible = forceColon ?? (now.Millisecond < 500);

        int totalW = 0;
        for (int i = 0; i < timeStr.Length; i++)
        {
            char ch = timeStr[i];
            int w = CrispClockFont2px.TryGetValue(ch, out var g) ? g[0].Length : 2;
            totalW += w + (i < timeStr.Length - 1 ? 1 : 0);
        }

        int startX = Math.Max(0, (32 - totalW) / 2);
        int startY = 3;

        int curX = startX;
        for (int i = 0; i < timeStr.Length; i++)
        {
            char ch = timeStr[i];
            if (!CrispClockFont2px.TryGetValue(ch, out var glyph))
            {
                curX += 2;
                continue;
            }

            if (ch == ':' && !colonVisible)
            {
                curX += glyph[0].Length + 1;
                continue;
            }

            for (int row = 0; row < glyph.Length; row++)
            {
                for (int col = 0; col < glyph[row].Length; col++)
                {
                    if (glyph[row][col] == 1)
                    {
                        SetPixel(rgb, curX + col, startY + row, 255, 255, 255, 1.0f);
                    }
                }
            }

            curX += glyph[0].Length + 1;
        }
    }

    private static void DrawPage1_Temp(byte[] rgb, int cpuTemp, int gpuTemp, int cpuLoad, int gpuLoad)
    {
        Array.Clear(rgb, 0, rgb.Length);

        DrawCrispText(rgb, 2, 2, "CPU", 0, 230, 255);
        string cpuVal = cpuTemp > 0 ? cpuTemp.ToString() : "--";
        DrawValueWithUnit(rgb, 30, 2, cpuVal, "\u00B0C", 255, 140, 40);

        DrawCrispText(rgb, 2, 10, "GPU", 200, 80, 255);
        string gpuVal = gpuTemp > 0 ? gpuTemp.ToString() : "--";
        DrawValueWithUnit(rgb, 30, 10, gpuVal, "\u00B0C", 255, 140, 40);
    }

    private static void DrawPage2_Watt(byte[] rgb, int cpuWatt, int gpuWatt, int cpuLoad, int gpuLoad)
    {
        Array.Clear(rgb, 0, rgb.Length);

        DrawCrispText(rgb, 2, 2, "CPU", 0, 230, 255);
        string cpuVal = cpuWatt > 0 ? cpuWatt.ToString() : "0";
        DrawValueWithUnit(rgb, 30, 2, cpuVal, "W", 255, 215, 30);

        DrawCrispText(rgb, 2, 10, "GPU", 200, 80, 255);
        string gpuVal = gpuWatt > 0 ? gpuWatt.ToString() : "0";
        DrawValueWithUnit(rgb, 30, 10, gpuVal, "W", 255, 215, 30);
    }

    private static void DrawPage3_Load(byte[] rgb, int cpuLoad, int gpuLoad)
    {
        Array.Clear(rgb, 0, rgb.Length);

        DrawCrispText(rgb, 2, 2, "CPU", 0, 230, 255);
        string cpuVal = cpuLoad.ToString();
        DrawValueWithUnit(rgb, 30, 2, cpuVal, "%", 50, 235, 110);

        DrawCrispText(rgb, 2, 10, "GPU", 200, 80, 255);
        string gpuVal = gpuLoad.ToString();
        DrawValueWithUnit(rgb, 30, 10, gpuVal, "%", 50, 235, 110);
    }

    private static void DrawScreenSciFiOrb(byte[] rgb, int cpuTemp, int cpuWatt, int cpuLoad, int gpuTemp, int gpuWatt, int gpuLoad)
    {
        for (int y = 0; y < 17; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                bool isFlank = (x < 6 || x > 25);
                if (isFlank)
                {
                    bool isHexBorder = ((x + y * 2) % 4 == 0) || ((x - y * 2 + 100) % 4 == 0);
                    if (isHexBorder) SetPixel(rgb, x, y, 20, 26, 38, 1.0f);
                    else SetPixel(rgb, x, y, 5, 7, 12, 1.0f);
                }
                else SetPixel(rgb, x, y, 4, 6, 12, 1.0f);
            }
        }

        for (int x = 1; x <= 9; x++)
        {
            SetPixel(rgb, x, 1, 10, 20, 35, 0.75f);
            SetPixel(rgb, x, 2, 12, 24, 42, 0.85f);
            SetPixel(rgb, x, 3, 8, 16, 28, 0.65f);
            SetPixel(rgb, x, 13, 8, 16, 28, 0.65f);
            SetPixel(rgb, x, 14, 12, 24, 42, 0.85f);
            SetPixel(rgb, x, 15, 10, 20, 35, 0.75f);
        }
        SetPixel(rgb, 1, 2, 0, 180, 255, 0.9f);
        SetPixel(rgb, 9, 2, 0, 180, 255, 0.9f);
        SetPixel(rgb, 1, 14, 0, 180, 255, 0.9f);
        SetPixel(rgb, 9, 14, 0, 180, 255, 0.9f);

        for (int x = 22; x <= 30; x++)
        {
            SetPixel(rgb, x, 1, 25, 10, 35, 0.75f);
            SetPixel(rgb, x, 2, 35, 15, 45, 0.85f);
            SetPixel(rgb, x, 3, 20, 8, 28, 0.65f);
            SetPixel(rgb, x, 13, 20, 8, 28, 0.65f);
            SetPixel(rgb, x, 14, 35, 15, 45, 0.85f);
            SetPixel(rgb, x, 15, 25, 10, 35, 0.75f);
        }
        SetPixel(rgb, 22, 2, 190, 60, 255, 0.9f);
        SetPixel(rgb, 30, 2, 190, 60, 255, 0.9f);
        SetPixel(rgb, 22, 14, 190, 60, 255, 0.9f);
        SetPixel(rgb, 30, 14, 190, 60, 255, 0.9f);

        float ocx = 15.5f, ocy = 8.0f;
        for (int y = 0; y < 17; y++)
        {
            for (int x = 9; x <= 22; x++)
            {
                float dx = (x - ocx);
                float dy = (y - ocy) * 1.35f;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                float core = MathF.Exp(-d * d / 3.8f);
                if (core > 0.03f) SetPixel(rgb, x, y, 40, 190, 255, core * 0.75f);

                float corona = MathF.Exp(-MathF.Pow(d - 4.2f, 2) / 2.8f);
                if (corona > 0.03f) SetPixel(rgb, x, y, 145, 45, 240, corona * 0.6f);

                float ring1 = MathF.Abs(d - 3.7f);
                if (ring1 < 0.9f) SetPixel(rgb, x, y, 190, 225, 255, (1.0f - ring1 / 0.9f) * 0.7f);
            }
        }

        var now = DateTime.Now;
        DrawCrispText(rgb, 11, 4, now.ToString("HH:mm"), 255, 255, 255);

        DrawCrispText(rgb, 3, 1, "CPU", 255, 255, 255);
        float ccx = 7.0f, ccy = 8.5f;
        for (int y = 4; y <= 13; y++)
        {
            for (int x = 3; x <= 11; x++)
            {
                float d = MathF.Sqrt((x - ccx) * (x - ccx) + (y - ccy) * (y - ccy));
                float ringDist = MathF.Abs(d - 3.1f);
                if (ringDist < 1.1f) SetPixel(rgb, x, y, 160, 50, 255, MathF.Exp(-ringDist * ringDist / 0.45f) * 0.92f);
            }
        }
        string cpuTStr = cpuTemp > 0 ? (cpuTemp < 10 ? "0" + cpuTemp : "" + cpuTemp) : "--";
        DrawCrispText(rgb, 5, 6, cpuTStr, 255, 255, 255);
        SetPixel(rgb, 9, 6, 160, 50, 255, 0.9f);
        string cpuWattStr = cpuWatt + "W";
        DrawCrispText(rgb, 2, 13, cpuWattStr, 200, 240, 255);
        DrawCrispText(rgb, 0, 6, cpuLoad < 10 ? " " + cpuLoad : "" + cpuLoad, 255, 255, 255);
        SetPixel(rgb, 1, 11, 0, 220, 255, 0.9f);

        DrawCrispText(rgb, 24, 1, "GPU", 255, 255, 255);
        float gcx = 24.0f, gcy = 8.5f;
        for (int y = 4; y <= 13; y++)
        {
            for (int x = 20; x <= 28; x++)
            {
                float d = MathF.Sqrt((x - gcx) * (x - gcx) + (y - gcy) * (y - gcy));
                float ringDist = MathF.Abs(d - 3.1f);
                if (ringDist < 1.1f) SetPixel(rgb, x, y, 160, 50, 255, MathF.Exp(-ringDist * ringDist / 0.45f) * 0.92f);
            }
        }
        string gpuTStr = gpuTemp > 0 ? (gpuTemp < 10 ? "0" + gpuTemp : "" + gpuTemp) : "--";
        DrawCrispText(rgb, 22, 6, gpuTStr, 255, 255, 255);
        SetPixel(rgb, 26, 6, 160, 50, 255, 0.9f);
        string gpuWattStr = gpuWatt + "W";
        DrawCrispText(rgb, 23, 13, gpuWattStr, 200, 240, 255);
        DrawCrispText(rgb, 29, 6, gpuLoad < 10 ? " " + gpuLoad : "" + gpuLoad, 255, 255, 255);
        SetPixel(rgb, 30, 11, 200, 80, 255, 0.9f);
    }

    private const string TaskName = "HMHubScreenMonitor";

    private static bool IsAutoStartEnabled()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/query /tn \"{TaskName}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit();
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static void ToggleAutoStart(bool enable)
    {
        try
        {
            string exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ZNeoScreenService.exe");
            if (enable)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/create /tn \"{TaskName}\" /tr \"\\\"{exePath}\\\"\" /sc onlogon /rl HIGHEST /f",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var p = Process.Start(psi);
                p?.WaitForExit();
            }
            else
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/delete /tn \"{TaskName}\" /f",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var p = Process.Start(psi);
                p?.WaitForExit();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to update auto-start setting: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public static void GeneratePreviewImages(string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        int scale = 20;
        int imgW = 32 * scale;
        int imgH = 17 * scale;

        void SaveFrame(byte[] rgb, string filename)
        {
            using var bmp = new Bitmap(imgW, imgH);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(5, 7, 12));
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                float dotRadius = scale * 0.42f;
                float offRadius = scale * 0.18f;
                using var offBrush = new SolidBrush(Color.FromArgb(20, 28, 45));

                for (int y = 0; y < 17; y++)
                {
                    for (int x = 0; x < 32; x++)
                    {
                        float cx = x * scale + scale / 2.0f;
                        float cy = y * scale + scale / 2.0f;
                        int idx = (y * 32 + x) * 3;
                        byte r = rgb[idx + 0];
                        byte gVal = rgb[idx + 1];
                        byte b = rgb[idx + 2];

                        if (r > 0 || gVal > 0 || b > 0)
                        {
                            using var onBrush = new SolidBrush(Color.FromArgb(r, gVal, b));
                            g.FillEllipse(onBrush, cx - dotRadius, cy - dotRadius, dotRadius * 2, dotRadius * 2);
                        }
                        else
                        {
                            g.FillEllipse(offBrush, cx - offRadius, cy - offRadius, offRadius * 2, offRadius * 2);
                        }
                    }
                }
            }
            bmp.Save(Path.Combine(targetDir, filename), System.Drawing.Imaging.ImageFormat.Png);
        }

        byte[] buf = new byte[32 * 17 * 3];

        DrawPage0_Clock(buf, true);
        SaveFrame(buf, "page0_clock_on.png");

        DrawPage0_Clock(buf, false);
        SaveFrame(buf, "page0_clock_off.png");

        DrawScreen(buf, 1, 45, 65, 18, 36, 120, 48, MonitorDisplayMode.CarouselAuto);
        SaveFrame(buf, "page1_temp.png");

        DrawScreen(buf, 2, 65, 78, 70, 33, 18, 15, MonitorDisplayMode.CarouselAuto);
        SaveFrame(buf, "page2_watt.png");

        DrawScreen(buf, 3, 85, 120, 92, 70, 210, 75, MonitorDisplayMode.CarouselAuto);
        SaveFrame(buf, "page3_load.png");

        using var stripBmp = new Bitmap(imgW, (imgH + 10) * 4);
        using (var sg = Graphics.FromImage(stripBmp))
        {
            sg.Clear(Color.FromArgb(8, 10, 16));

            for (int page = 0; page < 4; page++)
            {
                DrawScreen(buf, page, 45, 65, 18, 36, 120, 48, MonitorDisplayMode.CarouselAuto);
                using var tileBmp = new Bitmap(imgW, imgH);
                using (var tg = Graphics.FromImage(tileBmp))
                {
                    tg.Clear(Color.FromArgb(5, 7, 12));
                    tg.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    float dotRadius = scale * 0.42f;
                    float offRadius = scale * 0.18f;
                    using var offBrush = new SolidBrush(Color.FromArgb(20, 28, 45));
                    for (int y = 0; y < 17; y++)
                    {
                        for (int x = 0; x < 32; x++)
                        {
                            float cx = x * scale + scale / 2.0f;
                            float cy = y * scale + scale / 2.0f;
                            int idx = (y * 32 + x) * 3;
                            byte r = buf[idx + 0];
                            byte gVal = buf[idx + 1];
                            byte b = buf[idx + 2];
                            if (r > 0 || gVal > 0 || b > 0)
                            {
                                using var onBrush = new SolidBrush(Color.FromArgb(r, gVal, b));
                                tg.FillEllipse(onBrush, cx - dotRadius, cy - dotRadius, dotRadius * 2, dotRadius * 2);
                            }
                            else
                            {
                                tg.FillEllipse(offBrush, cx - offRadius, cy - offRadius, offRadius * 2, offRadius * 2);
                            }
                        }
                    }
                }
                sg.DrawImage(tileBmp, 0, page * (imgH + 10));
            }
            stripBmp.Save(Path.Combine(targetDir, "strip_white_values.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    private void ExitApp()
    {
        _running = false;
        _cts.Cancel();
        _notifyIcon.Visible = false;
        _hidStream?.Dispose();
        Application.Exit();
    }
}
