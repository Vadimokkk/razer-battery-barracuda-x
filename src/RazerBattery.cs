using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Reflection;

[assembly: AssemblyTitle("Razer Battery")]
[assembly: AssemblyDescription("Tray battery monitor for Razer Barracuda X")]
[assembly: AssemblyCompany("Razer Battery independent utility")]
[assembly: AssemblyProduct("Razer Battery")]
[assembly: AssemblyVersion("1.3.2.0")]
[assembly: AssemblyFileVersion("1.3.2.0")]

namespace RazerBatteryApp
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayContext(args));
        }
    }

    internal sealed class CachedReading
    {
        public int Percent;
        public DateTime Time;
        public bool Exact;
    }

    internal sealed class TrayContext : ApplicationContext
    {
        const string ProductName = "Razer Battery";
        const string SettingsKey = @"Software\RazerBattery";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        readonly NotifyIcon tray;
        readonly BatteryWindow window;
        readonly System.Windows.Forms.Timer resultTimer;
        readonly System.Windows.Forms.Timer refreshTimer;
        readonly ToolStripMenuItem startupItem;
        readonly ToolStripMenuItem alertItem;
        readonly ToolStripMenuItem microphoneMuteItem;
        readonly ToolStripMenuItem[] thresholdItems;
        readonly ToolStripMenuItem[] intervalItems;
        readonly DrainTracker drainTracker = new DrainTracker();
        readonly BatteryHistoryStore batteryHistory = new BatteryHistoryStore();
        BatteryHistoryWindow historyWindow;
        Icon currentIcon;
        bool allowClose;
        bool busyShown;
        bool lowAlertSent;
        bool updatingStartup;
        int resultVersion;
        int threshold;
        int intervalSeconds;
        bool alertsEnabled;
        bool wasCharging;
        CachedReading cached;

        public TrayContext(string[] args)
        {
            threshold = ReadSettingInt("AlertThreshold", 20, 10, 40);
            intervalSeconds = ReadSettingInt("RefreshSeconds", 60, 30, 120);
            alertsEnabled = ReadSettingBool("AlertsEnabled", true);
            cached = ReadCachedReading();
            bool startAtLogin = IsStartupEnabled();

            window = new BatteryWindow(StartReading, ShowHistory, intervalSeconds);
            window.FormClosing += HandleWindowClosing;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Открыть индикатор", null, delegate { ShowWindow(); });
            menu.Items.Add("Обновить заряд сейчас", null, delegate { StartReading(); });
            microphoneMuteItem = new ToolStripMenuItem("Заглушить микрофон Windows");
            microphoneMuteItem.Click += delegate
            {
                bool muted;
                string error;
                if (!WindowsAudioMute.TryToggleDefaultCaptureMute(out muted, out error))
                    MessageBox.Show("Не удалось переключить микрофон Windows: " + error, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                UpdateMicrophoneMuteItem();
            };
            menu.Items.Add(microphoneMuteItem);
            menu.Opening += delegate { UpdateMicrophoneMuteItem(); };
            menu.Items.Add(new ToolStripSeparator());

            startupItem = new ToolStripMenuItem("Запускать вместе с Windows") { CheckOnClick = true, Checked = startAtLogin };
            startupItem.CheckedChanged += delegate { if (!updatingStartup) SetStartup(startupItem.Checked); };
            menu.Items.Add(startupItem);

            alertItem = new ToolStripMenuItem("Напоминать о низком заряде") { CheckOnClick = true, Checked = alertsEnabled };
            alertItem.CheckedChanged += delegate
            {
                alertsEnabled = alertItem.Checked;
                WriteSetting("AlertsEnabled", alertsEnabled ? 1 : 0);
            };
            menu.Items.Add(alertItem);

            ToolStripMenuItem thresholdMenu = new ToolStripMenuItem("Порог напоминания");
            thresholdItems = new ToolStripMenuItem[] { AddRadioItem(thresholdMenu, "10%", 10, true), AddRadioItem(thresholdMenu, "20%", 20, true), AddRadioItem(thresholdMenu, "30%", 30, true), AddRadioItem(thresholdMenu, "40%", 40, true) };
            menu.Items.Add(thresholdMenu);

            ToolStripMenuItem intervalMenu = new ToolStripMenuItem("Обновлять заряд");
            intervalItems = new ToolStripMenuItem[] { AddRadioItem(intervalMenu, "Каждые 30 секунд", 30, false), AddRadioItem(intervalMenu, "Каждую минуту", 60, false), AddRadioItem(intervalMenu, "Каждые 2 минуты", 120, false) };
            menu.Items.Add(intervalMenu);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выход", null, delegate { ExitApplication(); });

            currentIcon = BatteryIcon.Create(-1, false, true);
            tray = new NotifyIcon();
            tray.Icon = currentIcon;
            tray.Text = "Razer Barracuda X · ищу заряд";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowWindow(); };

            resultTimer = new System.Windows.Forms.Timer();
            resultTimer.Interval = 300;
            resultTimer.Tick += delegate { CheckResult(); };
            resultTimer.Start();

            refreshTimer = new System.Windows.Forms.Timer();
            refreshTimer.Interval = intervalSeconds * 1000;
            refreshTimer.Tick += delegate { StartReading(); };
            refreshTimer.Start();

            window.SetRefreshInterval(intervalSeconds);
            if (Array.IndexOf(args, "--background") < 0) ShowWindow();
            StartReading();
            SelectRadioItems();
        }

        ToolStripMenuItem AddRadioItem(ToolStripMenuItem parent, string text, int value, bool isThreshold)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text) { CheckOnClick = true, Tag = value };
            item.Click += delegate
            {
                foreach (ToolStripItem child in parent.DropDownItems)
                    if (child is ToolStripMenuItem) ((ToolStripMenuItem)child).Checked = Object.Equals(((ToolStripMenuItem)child).Tag, value);
                if (isThreshold)
                {
                    threshold = value;
                    WriteSetting("AlertThreshold", value);
                    lowAlertSent = false;
                }
                else
                {
                    intervalSeconds = value;
                    refreshTimer.Interval = value * 1000;
                    window.SetRefreshInterval(value);
                    WriteSetting("RefreshSeconds", value);
                }
            };
            parent.DropDownItems.Add(item);
            return item;
        }

        void SelectRadioItems()
        {
            foreach (ToolStripMenuItem item in thresholdItems) item.Checked = (int)item.Tag == threshold;
            foreach (ToolStripMenuItem item in intervalItems) item.Checked = (int)item.Tag == intervalSeconds;
        }

        void ShowWindow()
        {
            if (!window.Visible) window.Show();
            if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
            window.Activate();
        }

        void ShowHistory()
        {
            if (historyWindow == null || historyWindow.IsDisposed)
            {
                historyWindow = new BatteryHistoryWindow(batteryHistory);
                historyWindow.FormClosing += delegate { if (historyWindow != null) historyWindow = null; };
            }
            if (!historyWindow.Visible) historyWindow.Show();
            if (historyWindow.WindowState == FormWindowState.Minimized) historyWindow.WindowState = FormWindowState.Normal;
            historyWindow.Activate();
        }

        void UpdateMicrophoneMuteItem()
        {
            bool muted;
            string error;
            if (!WindowsAudioMute.TryReadDefaultCaptureMute(out muted, out error))
            {
                microphoneMuteItem.Checked = false;
                microphoneMuteItem.Text = "Микрофон Windows недоступен";
                microphoneMuteItem.Enabled = false;
                return;
            }
            microphoneMuteItem.Enabled = true;
            microphoneMuteItem.Checked = muted;
            microphoneMuteItem.Text = muted ? "Включить микрофон по умолчанию" : "Заглушить микрофон по умолчанию";
        }

        void StartReading()
        {
            if (RazerBarracudaBattery.Monitor.IsBusy) return;
            busyShown = true;
            window.SetReadingInProgress(true);
            RazerBarracudaBattery.Monitor.BeginQuery();
        }

        void CheckResult()
        {
            if (RazerBarracudaBattery.Monitor.IsBusy) return;
            int currentVersion = RazerBarracudaBattery.Monitor.Version;
            if (currentVersion == resultVersion) return;
            resultVersion = currentVersion;
            busyShown = false;
            ApplyReading(RazerBarracudaBattery.Monitor.LastResult);
        }

        void ApplyReading(RazerBarracudaBattery.BatteryResult reading)
        {
            if (reading == null) return;
            bool fresh = reading.Percent >= 0 && (reading.State == "online" || reading.State == "bluetooth" || reading.State == "charging");
            bool charging = reading.Charging || reading.State == "charging";
            string source = reading.State == "bluetooth" ? "Bluetooth" : "USB-донгл";
            if (fresh)
            {
                cached = new CachedReading { Percent = reading.Percent, Time = DateTime.Now, Exact = reading.Exact };
                SaveCachedReading(cached);
                batteryHistory.Add(reading.Percent, reading.Exact, charging, source, DateTime.UtcNow);
                if (historyWindow != null && !historyWindow.IsDisposed) historyWindow.UpdateHistory();
                if (reading.Percent > threshold + 4) lowAlertSent = false;
                if (!charging && alertsEnabled && reading.Percent <= threshold && !lowAlertSent)
                {
                    lowAlertSent = true;
                    System.Media.SystemSounds.Exclamation.Play();
                    tray.BalloonTipTitle = "Пора подзарядить Barracuda X";
                    tray.BalloonTipText = String.Format("Осталось {0}{1}%. Порог напоминания: {2}%.", reading.Exact ? "" : "примерно ", reading.Percent, threshold);
                    tray.BalloonTipIcon = ToolTipIcon.Warning;
                    tray.ShowBalloonTip(5000);
                }
            }

            bool stale = !fresh && cached != null;
            int displayed = fresh ? reading.Percent : stale ? cached.Percent : -1;
            DrainEstimate estimate = null;
            if (fresh && !charging) estimate = drainTracker.Add(reading.Percent, source, DateTime.Now, batteryHistory.GetRecent(6 * 60));
            if (wasCharging && !charging && fresh && reading.Percent >= 95)
            {
                tray.BalloonTipTitle = "Barracuda X заряжены";
                tray.BalloonTipText = "Похоже, гарнитура набрала полный заряд. Можно отключить кабель.";
                tray.BalloonTipIcon = ToolTipIcon.Info;
                tray.ShowBalloonTip(4500);
            }
            wasCharging = charging;
            bool connected = fresh || reading.State == "charging";
            bool exact = fresh ? reading.Exact : stale && cached != null && cached.Exact;
            SetTrayIcon(displayed, charging, !connected);
            UpdateTooltip(reading, displayed, stale, exact);
            window.SetReading(reading, cached, intervalSeconds, busyShown, estimate);
        }

        void SetTrayIcon(int percent, bool charging, bool stale)
        {
            Icon next = BatteryIcon.Create(percent, charging, stale);
            Icon previous = currentIcon;
            currentIcon = next;
            tray.Icon = next;
            if (previous != null) previous.Dispose();
        }

        void UpdateTooltip(RazerBarracudaBattery.BatteryResult reading, int percent, bool stale, bool exact)
        {
            string text;
            if (reading.Charging || reading.State == "charging")
                text = percent >= 0 ? String.Format("Barracuda X · ⚡ {0}{1}%{2}", exact ? "" : "~", percent, stale ? " · последний замер" : "") : "Barracuda X · ⚡ зарядка";
            else if (percent >= 0) text = String.Format("Barracuda X · {0}{1}%{2}", exact ? "" : "~", percent, stale ? " · последний замер" : "");
            else text = "Barracuda X · заряд недоступен";
            if (text.Length > 63) text = text.Substring(0, 63);
            tray.Text = text;
        }

        void HandleWindowClosing(object sender, FormClosingEventArgs e)
        {
            if (!allowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                window.Hide();
            }
        }

        void SetStartup(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (enabled) key.SetValue(ProductName, "\"" + Application.ExecutablePath + "\" --background");
                    else key.DeleteValue(ProductName, false);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось изменить автозапуск: " + ex.Message, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                updatingStartup = true;
                startupItem.Checked = IsStartupEnabled();
                updatingStartup = false;
            }
        }

        bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return false;
                    string value = Convert.ToString(key.GetValue(ProductName, ""));
                    return value.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        static int ReadSettingInt(string name, int fallback, int min, int max)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
                {
                    int value = Convert.ToInt32(key.GetValue(name, fallback));
                    return Math.Max(min, Math.Min(max, value));
                }
            }
            catch { return fallback; }
        }

        static bool ReadSettingBool(string name, bool fallback)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
                    return Convert.ToInt32(key.GetValue(name, fallback ? 1 : 0)) != 0;
            }
            catch { return fallback; }
        }

        static void WriteSetting(string name, int value)
        {
            try { using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey)) key.SetValue(name, value, RegistryValueKind.DWord); }
            catch { }
        }

        CachedReading ReadCachedReading()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKey, false))
                {
                    if (key == null) return null;
                    int percent = Convert.ToInt32(key.GetValue("LastPercent", -1));
                    long ticks = Convert.ToInt64(key.GetValue("LastReadingTicks", 0L));
                    if (percent < 0 || percent > 100 || ticks <= 0) return null;
                    return new CachedReading { Percent = percent, Time = new DateTime(ticks), Exact = Convert.ToInt32(key.GetValue("LastExact", 0)) != 0 };
                }
            }
            catch { return null; }
        }

        static void SaveCachedReading(CachedReading reading)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
                {
                    key.SetValue("LastPercent", reading.Percent, RegistryValueKind.DWord);
                    key.SetValue("LastReadingTicks", reading.Time.Ticks, RegistryValueKind.QWord);
                    key.SetValue("LastExact", reading.Exact ? 1 : 0, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        void ExitApplication()
        {
            allowClose = true;
            resultTimer.Stop();
            refreshTimer.Stop();
            tray.Visible = false;
            tray.Dispose();
            if (currentIcon != null) currentIcon.Dispose();
            if (historyWindow != null && !historyWindow.IsDisposed) historyWindow.Close();
            window.Close();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (resultTimer != null) resultTimer.Dispose();
                if (refreshTimer != null) refreshTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class DrainEstimate
    {
        public double PercentPerHour;
        public double RemainingHours;
    }

    internal sealed class DrainTracker
    {
        public DrainEstimate Add(int percent, string source, DateTime now, IList<BatterySample> history)
        {
            if (percent <= 0 || history == null) return null;
            DateTime cutoff = now.ToUniversalTime().AddHours(-6);
            List<BatterySample> points = new List<BatterySample>();
            int previous = -1;
            bool haveExact = false;
            foreach (BatterySample sample in history)
                if (sample.Source == source && sample.Exact && !sample.Charging && sample.TimeUtc >= cutoff) { haveExact = true; break; }
            foreach (BatterySample sample in history)
            {
                if (sample.Source != source || sample.TimeUtc < cutoff || (haveExact && !sample.Exact)) continue;
                if (sample.Charging)
                {
                    points.Clear();
                    previous = -1;
                    continue;
                }
                if (previous >= 0 && sample.Percent >= previous + 3)
                {
                    points.Clear();
                    previous = -1;
                }
                points.Add(sample);
                previous = sample.Percent;
            }
            if (points.Count < 2) return null;

            BatterySample first = points[0];
            BatterySample last = points[points.Count - 1];
            double elapsed = (last.TimeUtc - first.TimeUtc).TotalMinutes;
            if (elapsed < 10 || first.Percent - last.Percent < 2 || last.Percent <= 0) return null;

            double meanX = 0, meanY = 0;
            foreach (BatterySample sample in points)
            {
                meanX += (sample.TimeUtc - first.TimeUtc).TotalHours;
                meanY += sample.Percent;
            }
            meanX /= points.Count;
            meanY /= points.Count;
            double numerator = 0, denominator = 0;
            foreach (BatterySample sample in points)
            {
                double x = (sample.TimeUtc - first.TimeUtc).TotalHours - meanX;
                double y = sample.Percent - meanY;
                numerator += x * y;
                denominator += x * x;
            }
            if (denominator <= 0) return null;
            double rate = -numerator / denominator;
            if (rate < 0.15 || rate > 45) return null;
            double remaining = percent / rate;
            if (remaining <= 0 || remaining > 72) return null;
            return new DrainEstimate { PercentPerHour = rate, RemainingHours = remaining };
        }
    }

    internal sealed class BatteryWindow : Form
    {
        readonly Action refreshAction;
        readonly Action historyAction;
        readonly BatteryGauge gauge;
        readonly Label badgeText;
        readonly Label status;
        readonly Label statusDetail;
        readonly Label etaValue;
        readonly Label etaDetail;
        readonly Label drainValue;
        readonly Label drainDetail;
        readonly Label footer;
        readonly Button refresh;
        readonly Button historyButton;
        readonly Panel statusPanel;
        readonly Color page = Color.FromArgb(9, 13, 23);
        readonly Color panel = Color.FromArgb(18, 26, 43);
        readonly Color line = Color.FromArgb(37, 49, 73);
        readonly Color green = Color.FromArgb(72, 224, 153);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public BatteryWindow(Action refreshAction, Action historyAction, int intervalSeconds)
        {
            this.refreshAction = refreshAction;
            this.historyAction = historyAction;
            Text = "Razer Battery";
            ClientSize = new Size(430, 590);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = page; ForeColor = Color.FromArgb(242, 246, 255);
            Font = new Font("Segoe UI", 9f);
            ShowInTaskbar = true;
            DoubleBuffered = true;

            Label eyebrow = LabelAt("BATTERY CONTROL", 24, 19, 180, 18, 8f, FontStyle.Bold, Color.FromArgb(122, 145, 180));
            eyebrow.AutoSize = true;
            Controls.Add(eyebrow);

            Panel badge = new Panel { BackColor = Color.FromArgb(21, 38, 37), Location = new Point(290, 16), Size = new Size(116, 28) };
            badge.Paint += DrawCardBorder;
            badgeText = LabelAt("SCANNING", 8, 0, 100, 28, 8f, FontStyle.Bold, green);
            badgeText.TextAlign = ContentAlignment.MiddleCenter;
            badge.Controls.Add(badgeText);
            Controls.Add(badge);

            Label title = LabelAt("Barracuda X", 24, 38, 380, 43, 22f, FontStyle.Bold, Color.FromArgb(245, 248, 255));
            Controls.Add(title);

            Label subtitle = LabelAt("Личный индикатор заряда", 26, 82, 360, 18, 9f, FontStyle.Regular, Color.FromArgb(142, 157, 184));
            Controls.Add(subtitle);

            gauge = new BatteryGauge { Location = new Point(121, 108), Size = new Size(188, 188), BackColor = page };
            Controls.Add(gauge);

            statusPanel = new Panel { Location = new Point(24, 310), Size = new Size(382, 66), BackColor = panel };
            statusPanel.Paint += DrawCardBorder;
            Label statusDot = LabelAt("●", 16, 15, 18, 28, 12f, FontStyle.Bold, green);
            statusDot.AutoSize = false;
            statusPanel.Controls.Add(statusDot);
            status = LabelAt("Подключи гарнитуру", 42, 10, 320, 22, 10f, FontStyle.Bold, Color.FromArgb(237, 242, 255));
            statusPanel.Controls.Add(status);
            statusDetail = LabelAt("Здесь появится состояние соединения", 42, 34, 320, 20, 8f, FontStyle.Regular, Color.FromArgb(135, 151, 179));
            statusPanel.Controls.Add(statusDetail);
            Controls.Add(statusPanel);

            Panel etaCard = NewMetricCard(24, 392, 185, 86);
            Label etaCaption = LabelAt("ОСТАЛОСЬ РАБОТЫ", 14, 11, 158, 15, 7.5f, FontStyle.Bold, Color.FromArgb(122, 145, 180));
            etaCard.Controls.Add(etaCaption);
            etaValue = LabelAt("Сбор данных", 14, 31, 158, 24, 13f, FontStyle.Bold, Color.FromArgb(239, 244, 255));
            etaCard.Controls.Add(etaValue);
            etaDetail = LabelAt("Жду снижения на 2%", 14, 58, 158, 16, 7.5f, FontStyle.Regular, Color.FromArgb(135, 151, 179));
            etaCard.Controls.Add(etaDetail);
            Controls.Add(etaCard);

            Panel drainCard = NewMetricCard(221, 392, 185, 86);
            Label drainCaption = LabelAt("РАСХОД БАТАРЕИ", 14, 11, 158, 15, 7.5f, FontStyle.Bold, Color.FromArgb(122, 145, 180));
            drainCard.Controls.Add(drainCaption);
            drainValue = LabelAt("—", 14, 31, 158, 24, 13f, FontStyle.Bold, Color.FromArgb(239, 244, 255));
            drainCard.Controls.Add(drainValue);
            drainDetail = LabelAt("темп за время наблюдения", 14, 58, 158, 16, 7.5f, FontStyle.Regular, Color.FromArgb(135, 151, 179));
            drainCard.Controls.Add(drainDetail);
            Controls.Add(drainCard);

            refresh = new Button();
            refresh.Text = "↻   ОБНОВИТЬ";
            refresh.Location = new Point(24, 495); refresh.Size = new Size(244, 46);
            refresh.FlatStyle = FlatStyle.Flat; refresh.FlatAppearance.BorderSize = 0;
            refresh.FlatAppearance.MouseOverBackColor = Color.FromArgb(101, 236, 174);
            refresh.BackColor = green; refresh.ForeColor = Color.FromArgb(8, 18, 14);
            refresh.Font = new Font("Segoe UI", 9f, FontStyle.Bold); refresh.Cursor = Cursors.Hand;
            refresh.Click += delegate { if (this.refreshAction != null) this.refreshAction(); };
            Controls.Add(refresh);

            historyButton = new Button();
            historyButton.Text = "▥   ИСТОРИЯ";
            historyButton.Location = new Point(280, 495); historyButton.Size = new Size(126, 46);
            historyButton.FlatStyle = FlatStyle.Flat; historyButton.FlatAppearance.BorderColor = line;
            historyButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(27, 39, 60);
            historyButton.BackColor = panel; historyButton.ForeColor = Color.FromArgb(221, 232, 250);
            historyButton.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold); historyButton.Cursor = Cursors.Hand;
            historyButton.Click += delegate { if (this.historyAction != null) this.historyAction(); };
            Controls.Add(historyButton);

            footer = LabelAt("Автообновление · настройки в меню значка трея", 24, 553, 382, 19, 8f, FontStyle.Regular, Color.FromArgb(122, 137, 164));
            footer.TextAlign = ContentAlignment.MiddleCenter;
            Controls.Add(footer);
            SetRefreshInterval(intervalSeconds);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); }
            catch { }
        }

        Label LabelAt(string text, int x, int y, int width, int height, float size, FontStyle style, Color color)
        {
            return new Label
            {
                Text = text, Location = new Point(x, y), Size = new Size(width, height),
                Font = new Font("Segoe UI", size, style), ForeColor = color,
                BackColor = Color.Transparent, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft
            };
        }

        Panel NewMetricCard(int x, int y, int width, int height)
        {
            Panel card = new Panel { Location = new Point(x, y), Size = new Size(width, height), BackColor = panel };
            card.Paint += DrawCardBorder;
            return card;
        }

        void DrawCardBorder(object sender, PaintEventArgs e)
        {
            Control control = (Control)sender;
            using (Pen pen = new Pen(line))
                e.Graphics.DrawRectangle(pen, 0, 0, control.Width - 1, control.Height - 1);
        }

        public void SetRefreshInterval(int seconds)
        {
            if (footer == null) return;
            string text = seconds < 60 ? "30 сек" : seconds == 60 ? "1 мин" : "2 мин";
            footer.Text = "Обновление: " + text + " · параметры — в меню трея";
        }

        public void SetReadingInProgress(bool busy)
        {
            refresh.Enabled = !busy;
            if (busy)
            {
                status.Text = "Считываю заряд…";
                statusDetail.Text = "Обычно это занимает пару секунд";
            }
        }

        public void SetReading(RazerBarracudaBattery.BatteryResult reading, CachedReading cached, int intervalSeconds, bool wasBusy, DrainEstimate estimate)
        {
            if (reading == null) return;
            refresh.Enabled = true;
            SetRefreshInterval(intervalSeconds);
            bool fresh = reading.Percent >= 0 && (reading.State == "online" || reading.State == "bluetooth" || reading.State == "charging");
            bool charging = reading.Charging || reading.State == "charging";
            if (fresh)
            {
                gauge.SetLevel(reading.Percent, reading.Exact, false, charging);
                badgeText.Text = charging ? "ЗАРЯДКА" : reading.State == "bluetooth" ? "BLUETOOTH" : "2,4 ГГЦ";
                badgeText.ForeColor = charging ? Color.DeepSkyBlue : green;
                status.Text = charging
                    ? (reading.ProductId == 0x0550 ? "Наушники заряжаются" : reading.Percent >= 100 ? "Кабель подключён · полный заряд" : "Кабель зарядки подключён")
                    : reading.State == "bluetooth" ? "Bluetooth подключён" : "USB-донгл подключён";
                statusDetail.Text = reading.Message;
                statusDetail.ForeColor = charging ? Color.DeepSkyBlue : Color.FromArgb(135, 151, 179);
                if (charging)
                {
                    SetEstimate(null);
                    etaValue.Text = "На зарядке";
                    etaDetail.Text = "Оценка обновится после отключения";
                    drainValue.Text = "Пауза";
                    drainDetail.Text = "На зарядке расход не считаю";
                }
                else SetEstimate(estimate);
            }
            else if (charging)
            {
                gauge.SetLevel(cached == null ? -1 : cached.Percent, cached != null && cached.Exact, cached != null, true);
                badgeText.Text = "ЗАРЯДКА";
                badgeText.ForeColor = Color.DeepSkyBlue;
                status.Text = reading.ProductId == 0x0550 && reading.ChargingKnown
                    ? "Наушники заряжаются"
                    : reading.ChargingKnown ? "Кабель зарядки подключён" : "Возможна зарядка";
                statusDetail.Text = reading.Message;
                statusDetail.ForeColor = Color.DeepSkyBlue;
                SetEstimate(null);
                etaValue.Text = "На зарядке";
                etaDetail.Text = "Оценка после отключения";
                drainValue.Text = "Пауза";
                drainDetail.Text = "Пауза на время зарядки";
            }
            else if (cached != null)
            {
                gauge.SetLevel(cached.Percent, cached.Exact, true, false);
                badgeText.Text = "ПОСЛЕДНИЙ ЗАМЕР";
                badgeText.ForeColor = Color.FromArgb(158, 169, 191);
                bool headsetLinked = reading.ProductId == 0x0550 && reading.ChargingKnown;
                status.Text = headsetLinked
                    ? reading.Charging ? "Гарнитура подключена · идёт зарядка" : "Гарнитура подключена · зарядки нет"
                    : "Сейчас беспроводной связи нет";
                statusDetail.Text = headsetLinked
                    ? "Последний уровень " + AgeText(DateTime.Now - cached.Time) + " назад"
                    : "Последний замер " + AgeText(DateTime.Now - cached.Time) + " назад";
                statusDetail.ForeColor = Color.FromArgb(135, 151, 179);
                etaValue.Text = "Нет связи";
                etaDetail.Text = "подключись для оценки";
                drainValue.Text = "—";
                drainDetail.Text = "нет новых замеров";
            }
            else
            {
                gauge.SetLevel(-1, false, false, false);
                badgeText.Text = "НЕТ СИГНАЛА";
                badgeText.ForeColor = Color.FromArgb(158, 169, 191);
                status.Text = "Не вижу гарнитуру";
                statusDetail.Text = "Подключи её по Bluetooth или через донгл 2,4 ГГц";
                statusDetail.ForeColor = Color.FromArgb(135, 151, 179);
                etaValue.Text = "—";
                etaDetail.Text = "пока нет данных";
                drainValue.Text = "—";
                drainDetail.Text = "ожидаю подключения";
            }
        }

        void SetEstimate(DrainEstimate estimate)
        {
            if (estimate == null)
            {
                etaValue.Text = "Сбор данных";
                etaDetail.Text = "Жду снижения на 2%";
                drainValue.Text = "—";
                drainDetail.Text = "оценка через 10+ мин";
                return;
            }
            etaValue.Text = "~" + FormatHours(estimate.RemainingHours);
            etaDetail.Text = "по текущему расходу";
            drainValue.Text = estimate.PercentPerHour.ToString("N1") + "% / ч";
            drainDetail.Text = "с начала наблюдения";
        }

        static string FormatHours(double hours)
        {
            int minutes = Math.Max(1, (int)Math.Round(hours * 60.0));
            if (minutes < 60) return minutes + " мин";
            int wholeHours = minutes / 60;
            int rest = minutes % 60;
            if (wholeHours >= 24) return (wholeHours / 24) + " д " + (wholeHours % 24) + " ч";
            return rest >= 10 ? wholeHours + " ч " + (rest / 10 * 10) + " мин" : wholeHours + " ч";
        }

        static string AgeText(TimeSpan age)
        {
            if (age.TotalMinutes < 1) return "меньше минуты";
            if (age.TotalHours < 1) return Math.Max(1, (int)age.TotalMinutes) + " мин";
            if (age.TotalDays < 1) return Math.Max(1, (int)age.TotalHours) + " ч";
            return Math.Max(1, (int)age.TotalDays) + " дн.";
        }
    }

    internal sealed class BatteryGauge : Control
    {
        int percent = -1;
        bool exact;
        bool stale;
        bool charging;

        public BatteryGauge()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public void SetLevel(int value, bool isExact, bool isStale, bool isCharging)
        {
            percent = value; exact = isExact; stale = isStale; charging = isCharging; Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float ring = 11f;
            RectangleF bounds = new RectangleF(18, 18, Width - 36, Height - 36);
            using (Pen track = new Pen(Color.FromArgb(31, 43, 64), ring))
            using (Pen active = new Pen(LevelColor(), ring))
            {
                track.StartCap = track.EndCap = LineCap.Round;
                active.StartCap = active.EndCap = LineCap.Round;
                g.DrawArc(track, bounds, -90, 359.8f);
                if (charging) g.DrawArc(active, bounds, -90, 112);
                else if (percent >= 0) g.DrawArc(active, bounds, -90, Math.Max(2, 359.8f * Math.Max(0, Math.Min(100, percent)) / 100f));
            }

            string number = percent < 0 ? (charging ? "⚡" : "—") : (exact ? "" : "~") + percent + "%";
            Color textColor = stale ? Color.FromArgb(166, 178, 200) : charging ? Color.DeepSkyBlue : LevelColor();
            using (Font valueFont = new Font(charging && percent < 0 ? "Segoe UI Symbol" : "Segoe UI", charging && percent < 0 ? 36f : 29f, FontStyle.Bold, GraphicsUnit.Point))
            using (SolidBrush brush = new SolidBrush(textColor))
            using (StringFormat format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(number, valueFont, brush, new RectangleF(12, 58, Width - 24, 52), format);

            if (charging && percent >= 0)
            {
                using (Font boltFont = new Font("Segoe UI Symbol", 12f, FontStyle.Bold, GraphicsUnit.Point))
                using (SolidBrush boltBrush = new SolidBrush(Color.DeepSkyBlue))
                    g.DrawString("⚡", boltFont, boltBrush, new RectangleF(Width / 2 + 43, 67, 20, 24));
            }

            string caption = charging ? "ЗАРЯДКА ПОДКЛЮЧЕНА" : stale ? "ПОСЛЕДНИЙ ЗАМЕР" : percent < 0 ? "НЕТ СИГНАЛА" : exact ? "ТОЧНЫЙ УРОВЕНЬ" : "ОЦЕНКА · ДОНГЛ";
            using (Font captionFont = new Font("Segoe UI", 7.2f, FontStyle.Bold, GraphicsUnit.Point))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(132, 149, 178)))
            using (StringFormat format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(caption, captionFont, brush, new RectangleF(10, 114, Width - 20, 22), format);
        }

        Color LevelColor()
        {
            if (charging) return Color.DeepSkyBlue;
            if (percent < 0) return Color.FromArgb(105, 120, 148);
            if (percent <= 20) return Color.FromArgb(255, 91, 106);
            if (percent <= 45) return Color.FromArgb(255, 184, 78);
            return Color.FromArgb(72, 224, 153);
        }
    }
    internal static class BatteryIcon
    {
        [DllImport("user32.dll", SetLastError = true)] static extern bool DestroyIcon(IntPtr hIcon);

        public static Icon Create(int percent, bool charging, bool stale)
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.Clear(Color.Magenta);
                    Color color = stale ? Color.Gray : charging ? Color.DeepSkyBlue : percent < 0 ? Color.Gray : percent <= 20 ? Color.IndianRed : percent <= 45 ? Color.Goldenrod : Color.MediumSeaGreen;
                    using (Pen pen = new Pen(color, 2f))
                    using (SolidBrush body = new SolidBrush(Color.FromArgb(18, 26, 43)))
                    using (SolidBrush terminal = new SolidBrush(color))
                    {
                        graphics.FillRoundedRectangle(body, 2, 4, 27, 24, 4);
                        graphics.DrawRoundedRectangle(pen, 2, 4, 27, 24, 4);
                        graphics.FillRoundedRectangle(terminal, 28, 11, 3, 10, 1);
                    }

                    string label = percent < 0 ? "—" : Math.Max(0, Math.Min(100, percent)).ToString();
                    float labelSize = percent >= 100 ? 10f : percent >= 10 ? 14f : 16f;
                    using (Font font = new Font("Segoe UI", labelSize, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (SolidBrush brush = new SolidBrush(Color.White))
                    using (StringFormat format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        graphics.DrawString(label, font, brush, new RectangleF(3, 5, 24, 20), format);

                    using (Font percentFont = new Font("Segoe UI", 5.5f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (SolidBrush percentBrush = new SolidBrush(Color.FromArgb(205, 218, 238)))
                        graphics.DrawString("%", percentFont, percentBrush, 21.5f, 21f);

                    if (charging)
                    {
                        Point[] bolt = { new Point(27, 1), new Point(23, 6), new Point(26, 6), new Point(24, 10), new Point(30, 5), new Point(27, 5) };
                        using (SolidBrush brush = new SolidBrush(Color.Yellow)) graphics.FillPolygon(brush, bolt);
                    }
                }
                bitmap.MakeTransparent(Color.Magenta);
                IntPtr handle = bitmap.GetHicon();
                try { return (Icon)Icon.FromHandle(handle).Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
    }

    internal static class GraphicsExtensions
    {
        public static void FillRoundedRectangle(this Graphics graphics, Brush brush, int x, int y, int width, int height, int radius)
        {
            using (GraphicsPath path = RoundedPath(x, y, width, height, radius)) graphics.FillPath(brush, path);
        }
        public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, int x, int y, int width, int height, int radius)
        {
            using (GraphicsPath path = RoundedPath(x, y, width, height, radius)) graphics.DrawPath(pen, path);
        }
        static GraphicsPath RoundedPath(int x, int y, int width, int height, int radius)
        {
            int d = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + width - d, y, d, d, 270, 90);
            path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
            path.AddArc(x, y + height - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
