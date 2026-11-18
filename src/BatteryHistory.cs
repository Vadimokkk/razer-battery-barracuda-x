using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace RazerBatteryApp
{
    internal sealed class BatterySample
    {
        public DateTime TimeUtc;
        public int Percent;
        public bool Exact;
        public bool Charging;
        public string Source;
    }

    internal sealed class BatteryHistoryStore
    {
        const int RetentionDays = 30;
        readonly object gate = new object();
        readonly List<BatterySample> samples = new List<BatterySample>();
        readonly string filePath;
        DateTime lastTrimUtc;

        public BatteryHistoryStore()
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RazerBattery");
            filePath = Path.Combine(folder, "charge-history.csv");
            Load();
        }

        public void Add(int percent, bool exact, bool charging, string source, DateTime timeUtc)
        {
            if (percent < 0 || percent > 100) return;
            if (timeUtc.Kind != DateTimeKind.Utc) timeUtc = timeUtc.ToUniversalTime();
            if (String.IsNullOrEmpty(source)) source = "USB-донгл";
            lock (gate)
            {
                if (samples.Count > 0)
                {
                    BatterySample last = samples[samples.Count - 1];
                    if (last.Percent == percent && last.Exact == exact && last.Charging == charging && last.Source == source &&
                        timeUtc - last.TimeUtc < TimeSpan.FromMinutes(5)) return;
                }

                BatterySample sample = new BatterySample
                {
                    TimeUtc = timeUtc,
                    Percent = percent,
                    Exact = exact,
                    Charging = charging,
                    Source = source
                };
                samples.Add(sample);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                    File.AppendAllText(filePath, Format(sample) + Environment.NewLine, new System.Text.UTF8Encoding(false));
                }
                catch { }

                if (timeUtc - lastTrimUtc >= TimeSpan.FromHours(24)) Trim(timeUtc);
            }
        }

        public List<BatterySample> GetRecent(int hours)
        {
            DateTime cutoff = DateTime.UtcNow.AddHours(-hours);
            List<BatterySample> result = new List<BatterySample>();
            lock (gate)
                foreach (BatterySample sample in samples)
                    if (sample.TimeUtc >= cutoff) result.Add(sample);
            return result;
        }

        void Load()
        {
            lock (gate)
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        foreach (string row in File.ReadAllLines(filePath))
                        {
                            string[] parts = row.Split(',');
                            long ticks;
                            int percent;
                            if ((parts.Length != 3 && parts.Length != 5) || !Int64.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks) ||
                                !Int32.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out percent) || percent < 0 || percent > 100) continue;
                            DateTime time;
                            try { time = new DateTime(ticks, DateTimeKind.Utc); }
                            catch { continue; }
                            bool exact = parts[2] == "1";
                            bool charging = parts.Length == 5 && parts[3] == "1";
                            string source = parts.Length == 5 && !String.IsNullOrEmpty(parts[4]) ? parts[4] : exact ? "Bluetooth" : "USB-донгл";
                            samples.Add(new BatterySample { TimeUtc = time, Percent = percent, Exact = exact, Charging = charging, Source = source });
                        }
                    }
                    if (samples.Count > 0 && samples[0].TimeUtc < DateTime.UtcNow.AddDays(-RetentionDays)) Trim(DateTime.UtcNow);
                }
                catch { samples.Clear(); }
                lastTrimUtc = DateTime.UtcNow;
            }
        }

        void Trim(DateTime nowUtc)
        {
            DateTime cutoff = nowUtc.AddDays(-RetentionDays);
            samples.RemoveAll(delegate(BatterySample s) { return s.TimeUtc < cutoff; });
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                using (StreamWriter writer = new StreamWriter(filePath, false, new System.Text.UTF8Encoding(false)))
                    foreach (BatterySample sample in samples) writer.WriteLine(Format(sample));
            }
            catch { }
            lastTrimUtc = nowUtc;
        }

        static string Format(BatterySample sample)
        {
            return sample.TimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "," +
                sample.Percent.ToString(CultureInfo.InvariantCulture) + "," + (sample.Exact ? "1" : "0") + "," +
                (sample.Charging ? "1" : "0") + "," + (sample.Source ?? "USB-донгл");
        }
    }

    internal sealed class BatteryHistoryWindow : Form
    {
        readonly BatteryHistoryStore history;
        readonly BatteryHistoryGraph graph;
        readonly Label summary;
        readonly Button dayButton;
        readonly Button weekButton;
        int hours = 24;
        readonly Color page = Color.FromArgb(9, 13, 23);
        readonly Color panel = Color.FromArgb(18, 26, 43);
        readonly Color line = Color.FromArgb(37, 49, 73);
        readonly Color green = Color.FromArgb(72, 224, 153);

        public BatteryHistoryWindow(BatteryHistoryStore history)
        {
            this.history = history;
            Text = "Razer Battery · история";
            ClientSize = new Size(520, 450);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = page;
            ForeColor = Color.FromArgb(242, 246, 255);
            Font = new Font("Segoe UI", 9f);
            DoubleBuffered = true;
            ShowInTaskbar = false;

            Label eyebrow = NewLabel("BATTERY HISTORY", 24, 18, 190, 18, 8f, FontStyle.Bold, Color.FromArgb(122, 145, 180));
            Controls.Add(eyebrow);
            Label title = NewLabel("История заряда", 24, 39, 270, 36, 22f, FontStyle.Bold, Color.FromArgb(245, 248, 255));
            Controls.Add(title);
            dayButton = NewRangeButton("24 часа", 342, 25, 76);
            weekButton = NewRangeButton("7 дней", 424, 25, 72);
            dayButton.Click += delegate { hours = 24; UpdateHistory(); };
            weekButton.Click += delegate { hours = 24 * 7; UpdateHistory(); };
            Controls.Add(dayButton);
            Controls.Add(weekButton);

            Label subtitle = NewLabel("Заряд, который приложение замечало во время работы", 26, 80, 468, 19, 9f, FontStyle.Regular, Color.FromArgb(142, 157, 184));
            Controls.Add(subtitle);

            graph = new BatteryHistoryGraph { Location = new Point(24, 108), Size = new Size(472, 245), BackColor = panel };
            Controls.Add(graph);

            Panel summaryPanel = new Panel { Location = new Point(24, 368), Size = new Size(472, 43), BackColor = panel };
            summaryPanel.Paint += DrawBorder;
            summary = NewLabel("Подключи гарнитуру — начну записывать заряд", 14, 0, 444, 43, 8.5f, FontStyle.Regular, Color.FromArgb(193, 205, 226));
            summaryPanel.Controls.Add(summary);
            Controls.Add(summaryPanel);

            Label footer = NewLabel("Данные хранятся на этом компьютере · срок хранения — 30 дней", 24, 420, 472, 18, 8f, FontStyle.Regular, Color.FromArgb(122, 137, 164));
            footer.TextAlign = ContentAlignment.MiddleCenter;
            Controls.Add(footer);
            UpdateHistory();
        }

        Button NewRangeButton(string text, int x, int y, int width)
        {
            return new Button
            {
                Text = text, Location = new Point(x, y), Size = new Size(width, 30),
                FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(18, 26, 43),
                ForeColor = Color.FromArgb(224, 232, 247), Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                Cursor = Cursors.Hand, UseVisualStyleBackColor = false
            };
        }

        static Label NewLabel(string text, int x, int y, int width, int height, float size, FontStyle style, Color color)
        {
            return new Label
            {
                Text = text, Location = new Point(x, y), Size = new Size(width, height),
                Font = new Font("Segoe UI", size, style), ForeColor = color,
                BackColor = Color.Transparent, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft
            };
        }

        void DrawBorder(object sender, PaintEventArgs e)
        {
            Control control = (Control)sender;
            using (Pen pen = new Pen(line)) e.Graphics.DrawRectangle(pen, 0, 0, control.Width - 1, control.Height - 1);
        }

        public void UpdateHistory()
        {
            if (graph == null) return;
            dayButton.BackColor = hours == 24 ? Color.FromArgb(31, 53, 48) : Color.FromArgb(18, 26, 43);
            dayButton.ForeColor = hours == 24 ? green : Color.FromArgb(224, 232, 247);
            weekButton.BackColor = hours == 168 ? Color.FromArgb(31, 53, 48) : Color.FromArgb(18, 26, 43);
            weekButton.ForeColor = hours == 168 ? green : Color.FromArgb(224, 232, 247);
            List<BatterySample> points = history.GetRecent(hours);
            graph.SetSamples(points, hours);
            if (points.Count == 0)
            {
                summary.Text = "Подключи гарнитуру — начну записывать заряд";
                return;
            }
            BatterySample last = points[points.Count - 1];
            summary.Text = String.Format("Замеров: {0}   ·   Последний: {1}   ·   {2}{3}%",
                points.Count, last.TimeUtc.ToLocalTime().ToString("HH:mm"), last.Exact ? "" : "~", last.Percent);
        }
    }

    internal sealed class BatteryHistoryGraph : Control
    {
        readonly Color grid = Color.FromArgb(39, 51, 74);
        readonly Color muted = Color.FromArgb(132, 149, 178);
        readonly Color green = Color.FromArgb(72, 224, 153);
        readonly Color red = Color.FromArgb(255, 91, 106);
        List<BatterySample> samples = new List<BatterySample>();
        int hours = 24;

        public BatteryHistoryGraph()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public void SetSamples(List<BatterySample> values, int rangeHours)
        {
            samples = values ?? new List<BatterySample>();
            hours = rangeHours;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle plot = new Rectangle(48, 16, Math.Max(20, Width - 66), Math.Max(20, Height - 54));
            using (Pen border = new Pen(Color.FromArgb(37, 49, 73)))
                g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

            using (Font axisFont = new Font("Segoe UI", 7.5f, FontStyle.Regular))
            using (SolidBrush axisBrush = new SolidBrush(muted))
            using (Pen gridPen = new Pen(grid))
            using (Pen lowPen = new Pen(Color.FromArgb(100, red), 1f))
            {
                for (int percent = 0; percent <= 100; percent += 25)
                {
                    int y = plot.Bottom - (int)Math.Round(plot.Height * percent / 100.0);
                    g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                    g.DrawString(percent + "%", axisFont, axisBrush, 8, y - 8);
                }
                int lowY = plot.Bottom - (int)Math.Round(plot.Height * 0.2);
                lowPen.DashStyle = DashStyle.Dash;
                g.DrawLine(lowPen, plot.Left, lowY, plot.Right, lowY);

                DateTime now = DateTime.UtcNow;
                DateTime from = now.AddHours(-hours);
                string leftText = hours <= 24 ? from.ToLocalTime().ToString("HH:mm") : from.ToLocalTime().ToString("dd.MM");
                string rightText = hours <= 24 ? now.ToLocalTime().ToString("HH:mm") : now.ToLocalTime().ToString("dd.MM");
                g.DrawString(leftText, axisFont, axisBrush, plot.Left, plot.Bottom + 9);
                SizeF rightSize = g.MeasureString(rightText, axisFont);
                g.DrawString(rightText, axisFont, axisBrush, plot.Right - rightSize.Width, plot.Bottom + 9);
            }

            if (samples.Count == 0)
            {
                using (Font emptyFont = new Font("Segoe UI", 9f, FontStyle.Regular))
                using (SolidBrush emptyBrush = new SolidBrush(muted))
                using (StringFormat centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("Пока нет замеров", emptyFont, emptyBrush, plot, centered);
                return;
            }

            DateTime end = DateTime.UtcNow;
            DateTime start = end.AddHours(-hours);
            List<PointF> coordinates = new List<PointF>();
            List<List<PointF>> segments = new List<List<PointF>>();
            segments.Add(new List<PointF>());
            DateTime previous = DateTime.MinValue;
            foreach (BatterySample sample in samples)
            {
                if (previous != DateTime.MinValue && sample.TimeUtc - previous > TimeSpan.FromMinutes(15))
                    segments.Add(new List<PointF>());
                double ratio = (sample.TimeUtc - start).TotalSeconds / (end - start).TotalSeconds;
                ratio = Math.Max(0, Math.Min(1, ratio));
                float x = plot.Left + (float)(plot.Width * ratio);
                float y = plot.Bottom - (float)(plot.Height * sample.Percent / 100.0);
                PointF point = new PointF(x, y);
                coordinates.Add(point);
                segments[segments.Count - 1].Add(point);
                previous = sample.TimeUtc;
            }

            using (Pen path = new Pen(green, 2.4f))
            using (SolidBrush pointBrush = new SolidBrush(green))
            using (SolidBrush halo = new SolidBrush(Color.FromArgb(40, green)))
            {
                path.LineJoin = LineJoin.Round;
                foreach (List<PointF> segment in segments)
                    if (segment.Count > 1) g.DrawLines(path, segment.ToArray());
                int step = Math.Max(1, coordinates.Count / 80);
                for (int i = 0; i < coordinates.Count; i += step)
                {
                    PointF p = coordinates[i];
                    g.FillEllipse(halo, p.X - 5, p.Y - 5, 10, 10);
                    g.FillEllipse(pointBrush, p.X - 2, p.Y - 2, 4, 4);
                }
                if (coordinates.Count > 0)
                {
                    PointF p = coordinates[coordinates.Count - 1];
                    g.FillEllipse(halo, p.X - 7, p.Y - 7, 14, 14);
                    g.FillEllipse(pointBrush, p.X - 3, p.Y - 3, 6, 6);
                }
            }
        }
    }
}
