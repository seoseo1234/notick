using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Windows.Automation;

namespace InterparkNativeClicker
{
    public static class Program
    {
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

        public static void Run()
        {
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    internal sealed class SeatDot
    {
        public float X;
        public float Y;
        public float Diameter;
        public bool Available;
        public int Priority;
        public double Row;
        public double Along;
    }

    internal sealed class SeatChoice
    {
        public SeatDot Dot;
        public List<SeatDot> Group;
        public int Priority;
        public double Front;
        public bool Aisle;
        public int RunLength;
    }

    internal static class SeatDetector
    {
        public static double? NumberAxisAngle { get; private set; }
        public static void ResetNumberAxis() { NumberAxisAngle = null; }
        private static readonly int[][] PriorityColors = new int[][] {
            new[]{117,87,243}, new[]{8,169,30}, new[]{3,181,233}, new[]{255,114,72},
            new[]{139,206,52}, new[]{148,0,0}, new[]{255,204,16}, new[]{255,38,147},
            new[]{154,19,182}, new[]{136,231,138}, new[]{0,8,218}, new[]{252,67,9},
            new[]{232,165,123}, new[]{231,17,238}, new[]{233,116,32}, new[]{191,31,103},
            new[]{39,159,146}, new[]{115,170,245}, new[]{236,114,196}, new[]{188,202,35},
            new[]{161,253,251}, new[]{68,132,183}
        };

        public static List<SeatDot> Scan(Bitmap bitmap)
        {
            int width = bitmap.Width, height = bitmap.Height;
            var rect = new Rectangle(0, 0, width, height);
            var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            int stride = Math.Abs(data.Stride);
            byte[] bytes = new byte[stride * height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            bitmap.UnlockBits(data);
            if (!NumberAxisAngle.HasValue) NumberAxisAngle = DetectNumberAxis(bytes, width, height, stride);

            int size = width * height;
            var mask = new byte[size];
            var colored = new byte[size];
            for (int y = 0; y < height; y++)
            {
                int row = y * stride;
                int index = y * width;
                for (int x = 0; x < width; x++, index++)
                {
                    int p = row + x * 3;
                    int b = bytes[p], g = bytes[p + 1], r = bytes[p + 2];
                    int hi = Math.Max(r, Math.Max(g, b));
                    int lo = Math.Min(r, Math.Min(g, b));
                    int chroma = hi - lo;
                    bool isColor = chroma >= 42;
                    // Seat maps use pale gray dots for unavailable seats. They help us
                    // infer rows/aisles, but only colored dots may be clicked.
                    bool isGrayDot = chroma <= 25 && hi >= 180 && hi <= 248;
                    if (isColor || isGrayDot) mask[index] = 1;
                    if (isColor) colored[index] = 1;
                }
            }

            var result = new List<SeatDot>();
            var seen = new byte[size];
            var queue = new int[size];
            for (int start = 0; start < size; start++)
            {
                if (mask[start] == 0 || seen[start] != 0) continue;
                int head = 0, tail = 0;
                queue[tail++] = start;
                seen[start] = 1;
                int minX = width, minY = height, maxX = -1, maxY = -1;
                int pixels = 0, colorPixels = 0, sumR = 0, sumG = 0, sumB = 0;
                while (head < tail)
                {
                    int current = queue[head++];
                    int x = current % width, y = current / width;
                    int p = y * stride + x * 3;
                    pixels++;
                    if (colored[current] != 0)
                    {
                        colorPixels++;
                        sumB += bytes[p]; sumG += bytes[p + 1]; sumR += bytes[p + 2];
                    }
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                        int ni = ny * width + nx;
                        if (mask[ni] != 0 && seen[ni] == 0) { seen[ni] = 1; queue[tail++] = ni; }
                    }
                }
                int w = maxX - minX + 1, h = maxY - minY + 1;
                if (w < 5 || h < 5 || w > 90 || h > 90) continue;
                double ratio = (double)w / h;
                if (ratio < 0.68 || ratio > 1.47) continue;
                if (pixels < Math.Max(8, (int)(w * h * 0.07)) || pixels > w * h * 0.96) continue;
                bool available = colorPixels >= Math.Max(4, (int)(pixels * 0.035));
                int priority = PriorityColors.Length;
                if (available)
                {
                    int r = sumR / colorPixels, g = sumG / colorPixels, b = sumB / colorPixels;
                    double best = Double.MaxValue;
                    for (int i = 0; i < PriorityColors.Length; i++)
                    {
                        double dr = r - PriorityColors[i][0], dg = g - PriorityColors[i][1], db = b - PriorityColors[i][2];
                        double distance = Math.Sqrt(dr * dr + dg * dg + db * db);
                        if (distance < best) { best = distance; priority = i; }
                    }
                    if (best > 60) priority = PriorityColors.Length; // still selectable, lower preference
                }
                result.Add(new SeatDot { X = (minX + maxX) / 2f, Y = (minY + maxY) / 2f,
                    Diameter = (w + h) / 2f, Available = available, Priority = priority });
            }
            return result;
        }

        public static SeatChoice Choose(List<SeatDot> dots, List<PointF> attempted, int maxSeats)
        {
            if (dots == null || dots.Count == 0 || !NumberAxisAngle.HasValue) return null;
            var all = dots;
            var eligible = dots.Where(d => d.Available && !attempted.Any(p =>
                Distance(p.X, p.Y, d.X, d.Y) < Math.Max(7, d.Diameter * 0.72f))).ToList();
            if (eligible.Count == 0) return null;
            var diameters = all.Select(d => d.Diameter).OrderBy(d => d).ToArray();
            double typical = diameters[diameters.Length / 2];

            // Number labels define the row direction; seats lie perpendicular to it.
            double angle = NumberAxisAngle.Value;
            // The major axis follows row progression; the perpendicular minor
            // axis follows numbered seats within each row.
            double rowX = Math.Cos(angle), rowY = Math.Sin(angle);
            if ((Math.Abs(rowY) >= Math.Abs(rowX) && rowY < 0) ||
                (Math.Abs(rowY) < Math.Abs(rowX) && rowX < 0))
            { rowX = -rowX; rowY = -rowY; }
            double alongX = rowY, alongY = -rowX;
            double meanX = all.Average(d => d.X), meanY = all.Average(d => d.Y);
            foreach (SeatDot d in all) { d.Along = (d.X - meanX) * alongX + (d.Y - meanY) * alongY; d.Row = (d.X - meanX) * rowX + (d.Y - meanY) * rowY; }

            var rows = new List<List<SeatDot>>();
            foreach (SeatDot dot in all.OrderBy(d => d.Row))
            {
                List<SeatDot> row = rows.FirstOrDefault(r => Math.Abs(r[0].Row - dot.Row) <= Math.Max(6, typical * 0.72));
                if (row == null) { row = new List<SeatDot>(); rows.Add(row); }
                row.Add(dot);
            }

            var choices = new List<SeatChoice>();
            foreach (List<SeatDot> row in rows)
            {
                row.Sort((a, b) => a.Along.CompareTo(b.Along));
                var spacings = new List<double>();
                for (int i = 1; i < row.Count; i++)
                {
                    double gap = row[i].Along - row[i - 1].Along;
                    if (gap > typical * 0.75 && gap < typical * 2.7) spacings.Add(gap);
                }
                spacings.Sort();
                double seatSpacing = spacings.Count == 0 ? typical * 1.35 : spacings[spacings.Count / 2];
                List<SeatDot> run = new List<SeatDot>();
                for (int i = 0; i < row.Count; i++)
                {
                    SeatDot dot = row[i];
                    bool usable = dot.Available && eligible.Contains(dot);
                    bool adjacent = run.Count == 0 || (i > 0 && row[i - 1] == run[run.Count - 1] && dot.Along - run[run.Count - 1].Along <= seatSpacing * 1.55);
                    if (!usable || !adjacent) { AddChoices(choices, run, row, seatSpacing, maxSeats); run = new List<SeatDot>(); }
                    if (usable) run.Add(dot);
                }
                AddChoices(choices, run, row, seatSpacing, maxSeats);
            }
            if (choices.Count == 0) return null;
            choices.Sort((a, b) => {
                int c = b.Aisle.CompareTo(a.Aisle); if (c != 0) return c;
                c = b.RunLength.CompareTo(a.RunLength); if (c != 0) return c;
                c = a.Priority.CompareTo(b.Priority); if (c != 0) return c;
                return a.Dot.Along.CompareTo(b.Dot.Along);
            });
            return choices[0];
        }

        private static void AddChoices(List<SeatChoice> choices, List<SeatDot> run, List<SeatDot> row, double spacing, int maxSeats)
        {
            if (run == null || run.Count == 0) return;
            for (int length = Math.Min(maxSeats, run.Count); length >= 1; length--)
                for (int start = 0; start + length <= run.Count; start++)
                {
                    var group = run.Skip(start).Take(length).ToList();
                    SeatDot first = group[0], last = group[group.Count - 1];
                    int firstIndex = row.IndexOf(first), lastIndex = row.IndexOf(last);
                    double leftGap = firstIndex == 0 ? Double.MaxValue : first.Along - row[firstIndex - 1].Along;
                    double rightGap = lastIndex == row.Count - 1 ? Double.MaxValue : row[lastIndex + 1].Along - last.Along;
                    bool aisle = leftGap > spacing * 1.45 || rightGap > spacing * 1.45;
                    SeatDot pick = leftGap >= rightGap ? first : last;
                    choices.Add(new SeatChoice { Dot = pick, Group = group, Priority = group.Min(d => d.Priority),
                        Front = 0, Aisle = aisle, RunLength = length });
                }
        }

        private static double? DetectNumberAxis(byte[] bytes, int width, int height, int stride)
        {
            int size = width * height;
            var dark = new byte[size];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int p = y * stride + x * 3;
                int b = bytes[p], g = bytes[p + 1], r = bytes[p + 2];
                int hi = Math.Max(r, Math.Max(g, b)), lo = Math.Min(r, Math.Min(g, b));
                if (hi < 125 && hi - lo < 55) dark[y * width + x] = 1;
            }
            var seen = new byte[size]; var queue = new int[size]; var centers = new List<PointF>(); var glyphSizes = new List<double>();
            for (int start = 0; start < size; start++)
            {
                if (dark[start] == 0 || seen[start] != 0) continue;
                int head = 0, tail = 0, minX = width, minY = height, maxX = -1, maxY = -1;
                long sumX = 0, sumY = 0; queue[tail++] = start; seen[start] = 1;
                while (head < tail)
                {
                    int i = queue[head++], x = i % width, y = i / width;
                    sumX += x; sumY += y; minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy; if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                        int ni = ny * width + nx; if (dark[ni] != 0 && seen[ni] == 0) { seen[ni] = 1; queue[tail++] = ni; }
                    }
                }
                int w = maxX - minX + 1, h = maxY - minY + 1;
                if (tail >= 2 && tail <= 120 && w <= 24 && h <= 24)
                { centers.Add(new PointF((float)sumX / tail, (float)sumY / tail)); glyphSizes.Add(Math.Max(w, h)); }
            }
            if (centers.Count < 10) return null;
            glyphSizes.Sort(); double glyph = glyphSizes[glyphSizes.Count / 2];
            int[] votes = new int[72]; int vectorCount = 0;
            for (int i = 0; i < centers.Count; i++)
            {
                double nearest = Double.MaxValue, angle = 0; PointF a = centers[i];
                for (int j = 0; j < centers.Count; j++)
                {
                    if (i == j) continue;
                    double dx = centers[j].X - a.X, dy = centers[j].Y - a.Y, d = Math.Sqrt(dx*dx + dy*dy);
                    if (d < Math.Max(7, glyph * 1.25) || d > Math.Max(42, glyph * 7) || d >= nearest) continue;
                    nearest = d; angle = Math.Atan2(dy, dx);
                }
                if (nearest == Double.MaxValue) continue;
                while (angle < -Math.PI / 2) angle += Math.PI;
                while (angle >= Math.PI / 2) angle -= Math.PI;
                int bin = (int)Math.Round((angle + Math.PI / 2) * 72 / Math.PI) % 72;
                votes[bin]++; vectorCount++;
            }
            if (vectorCount < 8) return null;
            int best = 0; for (int i = 1; i < votes.Length; i++) if (votes[i] > votes[best]) best = i;
            int consensus = votes[best] + votes[(best + 71) % 72] + votes[(best + 1) % 72];
            if (consensus < Math.Max(6, vectorCount / 5)) return null;
            return ((best + 0.5) * Math.PI / 72) - Math.PI / 2;
        }

        private static double Distance(double x1, double y1, double x2, double y2)
        {
            double dx = x1 - x2, dy = y1 - y2; return Math.Sqrt(dx * dx + dy * dy);
        }
    }

    internal static class NativeMouse
    {
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

        public static void Click(int x, int y)
        {
            SetCursorPos(x, y);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(3);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        }
    }

    internal sealed class WindowInfo
    {
        public IntPtr Handle;
        public string Title;
        public override string ToString() { return Title; }
    }

    internal static class NativeWindows
    {
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, Keys key);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        public static bool TryFindCompleteButton(IntPtr hwnd, out Point center)
        {
            center = Point.Empty;
            try
            {
                AutomationElement root = AutomationElement.FromHandle(hwnd);
                if (root == null) return false;
                var buttons = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                foreach (AutomationElement button in buttons)
                {
                    var info = button.Current;
                    string label = (info.Name ?? "").Replace(" ", "").Replace("\r", "").Replace("\n", "");
                    if (!label.Contains("선택완료") || !info.IsEnabled || info.IsOffscreen) continue;
                    System.Windows.Rect bounds = info.BoundingRectangle;
                    if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) continue;
                    center = new Point((int)Math.Round(bounds.Left + bounds.Width / 2),
                        (int)Math.Round(bounds.Top + bounds.Height / 2));
                    return true;
                }
            }
            catch { }
            return false;
        }

        public static List<WindowInfo> BrowserWindows()
        {
            var result = new List<WindowInfo>();
            EnumWindows(delegate(IntPtr hwnd, IntPtr lParam) {
                if (!IsWindowVisible(hwnd)) return true;
                var text = new StringBuilder(512); GetWindowText(hwnd, text, text.Capacity);
                if (text.Length == 0) return true;
                uint pid; GetWindowThreadProcessId(hwnd, out pid);
                try
                {
                    string process = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant();
                    if (process == "chrome" || process == "msedge") result.Add(new WindowInfo { Handle = hwnd, Title = text.ToString() });
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            return result;
        }
    }

    internal sealed class RegionSelectForm : Form
    {
        private Point start;
        private Point current;
        private bool dragging;
        public Rectangle SelectedRegion { get; private set; }

        public RegionSelectForm()
        {
            Rectangle bounds = Screen.AllScreens.Select(s => s.Bounds).Aggregate(Rectangle.Union);
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual; Bounds = bounds;
            BackColor = Color.Black; Opacity = 0.30; TopMost = true; Cursor = Cursors.Cross; DoubleBuffered = true;
            KeyPreview = true; KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
            MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { start = e.Location; current = e.Location; dragging = true; Invalidate(); } };
            MouseMove += (s, e) => { if (dragging) { current = e.Location; Invalidate(); } };
            MouseUp += (s, e) => {
                if (!dragging) return;
                dragging = false; current = e.Location;
                Rectangle local = MakeRect(start, current);
                if (local.Width >= 80 && local.Height >= 80)
                {
                    Point screen = PointToScreen(local.Location);
                    SelectedRegion = new Rectangle(screen, local.Size); DialogResult = DialogResult.OK; Close();
                }
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (dragging)
            {
                Rectangle r = MakeRect(start, current);
                using (var pen = new Pen(Color.Lime, 3)) e.Graphics.DrawRectangle(pen, r);
                using (var brush = new SolidBrush(Color.FromArgb(55, Color.Lime))) e.Graphics.FillRectangle(brush, r);
            }
            using (var brush = new SolidBrush(Color.White)) e.Graphics.DrawString("좌석도만 드래그로 선택 · Esc 취소", new Font("Segoe UI", 20, FontStyle.Bold), brush, 30, 30);
        }

        private static Rectangle MakeRect(Point a, Point b)
        {
            return new Rectangle(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly ComboBox browser = new ComboBox();
        private readonly ComboBox seatCount = new ComboBox();
        private readonly Label regionLabel = new Label();
        private readonly Label status = new Label();
        private readonly Button chooseRegion = new Button();
        private readonly Button preview = new Button();
        private readonly Button start = new Button();
        private readonly Button stop = new Button();
        private readonly Timer timer = new Timer();
        private readonly List<PointF> attempted = new List<PointF>();
        private Rectangle mapRegion;
        private IntPtr targetWindow = IntPtr.Zero;
        private int maxSeats = 4, clickedSeats = 0;
        private bool running = false;
        private const int HOTKEY_ID = 7184, WM_HOTKEY = 0x0312;

        public MainForm()
        {
            Text = "포도알 네이티브 클릭 도우미"; Width = 560; Height = 350; MinimumSize = new Size(560, 350);
            StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 9F);
            var heading = new Label { Left = 18, Top = 16, Width = 510, Height = 26, Text = "좌석 화면에서 빈 좌석을 찾아 실제 마우스로 선택합니다.", Font = new Font("Segoe UI", 11, FontStyle.Bold) };
            Controls.Add(heading);
            Controls.Add(new Label { Left = 18, Top = 56, Width = 115, Text = "브라우저 창" });
            browser.Left = 130; browser.Top = 52; browser.Width = 330; browser.DropDownStyle = ComboBoxStyle.DropDownList; Controls.Add(browser);
            var refresh = new Button { Left = 468, Top = 50, Width = 65, Height = 27, Text = "새로고침" }; refresh.Click += (s,e) => LoadWindows(); Controls.Add(refresh);
            chooseRegion.Left = 18; chooseRegion.Top = 94; chooseRegion.Width = 150; chooseRegion.Height = 32; chooseRegion.Text = "좌석도 범위 지정";
            chooseRegion.Click += (s,e) => SelectRegion(); Controls.Add(chooseRegion);
            regionLabel.Left = 180; regionLabel.Top = 99; regionLabel.Width = 350; regionLabel.Text = "범위 미지정"; Controls.Add(regionLabel);
            Controls.Add(new Label { Left = 18, Top = 145, Width = 140, Text = "선택할 최대 좌석 수" });
            seatCount.Left = 170; seatCount.Top = 140; seatCount.Width = 80; seatCount.DropDownStyle = ComboBoxStyle.DropDownList;
            for (int i = 1; i <= 4; i++) seatCount.Items.Add(i.ToString()); seatCount.SelectedItem = "4"; Controls.Add(seatCount);
            preview.Left = 270; preview.Top = 138; preview.Width = 125; preview.Height = 30; preview.Text = "후보 미리 확인"; preview.Click += (s,e) => PreviewCandidates(); Controls.Add(preview);
            start.Left = 18; start.Top = 190; start.Width = 180; start.Height = 42; start.Text = "시작 (실제 클릭)"; start.BackColor = Color.FromArgb(40, 130, 65); start.ForeColor = Color.White;
            start.Click += (s,e) => StartRun(); Controls.Add(start);
            stop.Left = 212; stop.Top = 190; stop.Width = 120; stop.Height = 42; stop.Text = "중지 (F8)"; stop.Click += (s,e) => StopRun("사용자가 중지했습니다."); Controls.Add(stop);
            status.Left = 18; status.Top = 248; status.Width = 510; status.Height = 52; status.Text = "먼저 좌석도만 포함하도록 범위를 지정하세요. 실행 중 F8을 누르면 즉시 멈춥니다."; Controls.Add(status);
            timer.Interval = 1; timer.Tick += (s,e) => ScanAndClick();
            LoadWindows();
            FormClosing += (s,e) => { timer.Stop(); NativeWindows.UnregisterHotKey(Handle, HOTKEY_ID); };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeWindows.RegisterHotKey(Handle, HOTKEY_ID, 0, Keys.F8);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID) { StopRun("F8으로 중지했습니다."); return; }
            base.WndProc(ref m);
        }

        private void LoadWindows()
        {
            WindowInfo previous = browser.SelectedItem as WindowInfo;
            browser.Items.Clear();
            foreach (WindowInfo item in NativeWindows.BrowserWindows()) browser.Items.Add(item);
            if (browser.Items.Count > 0) browser.SelectedIndex = 0;
            if (previous != null)
                for (int i = 0; i < browser.Items.Count; i++) if (((WindowInfo)browser.Items[i]).Handle == previous.Handle) browser.SelectedIndex = i;
            if (browser.Items.Count == 0) status.Text = "Chrome 또는 Edge 창을 찾지 못했습니다.";
        }

        private void SelectRegion()
        {
            Hide(); System.Threading.Thread.Sleep(180);
            using (var picker = new RegionSelectForm())
            {
                if (picker.ShowDialog() == DialogResult.OK)
                {
                    mapRegion = picker.SelectedRegion;
                    SeatDetector.ResetNumberAxis();
                    regionLabel.Text = String.Format("{0} × {1} px", mapRegion.Width, mapRegion.Height);
                    status.Text = "좌석도와 가장자리의 열 번호를 범위에 포함하세요.";
                }
            }
            Show(); Activate();
        }

        private Bitmap CaptureRegionBitmap()
        {
            var bitmap = new Bitmap(mapRegion.Width, mapRegion.Height, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(bitmap)) g.CopyFromScreen(mapRegion.Location, Point.Empty, mapRegion.Size, CopyPixelOperation.SourceCopy);
            return bitmap;
        }

        private void PreviewCandidates()
        {
            if (mapRegion.Width <= 0 || mapRegion.Height <= 0) { MessageBox.Show("먼저 좌석도 범위를 지정하세요."); return; }
            try
            {
                SeatDetector.ResetNumberAxis();
                using (Bitmap bitmap = CaptureRegionBitmap())
                {
                    List<SeatDot> dots = SeatDetector.Scan(bitmap);
                    int available = dots.Count(d => d.Available);
                    status.Text = SeatDetector.NumberAxisAngle.HasValue
                        ? String.Format("열 번호 방향 인식 · 점 {0}개 · 색 좌석 후보 {1}개", dots.Count, available)
                        : "열 번호를 인식하지 못했습니다. 좌석 양끝의 숫자가 범위 안에 모두 보이게 다시 지정하세요.";
                }
            }
            catch (Exception ex) { MessageBox.Show("화면을 읽지 못했습니다: " + ex.Message); }
        }

        private void StartRun()
        {
            if (mapRegion.Width <= 0 || mapRegion.Height <= 0) { MessageBox.Show("먼저 좌석도 범위를 지정하세요."); return; }
            WindowInfo selected = browser.SelectedItem as WindowInfo;
            if (selected == null) { MessageBox.Show("Chrome 또는 Edge 창을 선택하세요."); return; }
            SeatDetector.ResetNumberAxis();
            maxSeats = Int32.Parse(seatCount.SelectedItem.ToString()); clickedSeats = 0; attempted.Clear();
            targetWindow = selected.Handle;
            // Do not restore a maximized Chrome window: SW_RESTORE would shrink it.
            if (NativeWindows.IsIconic(targetWindow)) NativeWindows.ShowWindow(targetWindow, 9);
            NativeWindows.SetForegroundWindow(targetWindow);
            System.Threading.Thread.Sleep(180);
            if (NativeWindows.GetForegroundWindow() != targetWindow)
            {
                MessageBox.Show("브라우저 창을 앞으로 가져오지 못했습니다. 브라우저를 직접 활성화한 뒤 다시 시작하세요."); return;
            }
            running = true; Hide(); timer.Start();
        }

        private void ScanAndClick()
        {
            if (!running) return;
            if (NativeWindows.GetForegroundWindow() != targetWindow) { StopRun("브라우저 포커스가 바뀌어 중지했습니다."); return; }
            try
            {
                using (Bitmap bitmap = CaptureRegionBitmap())
                {
                    List<SeatDot> dots = SeatDetector.Scan(bitmap);
                    if (!SeatDetector.NumberAxisAngle.HasValue) { StopRun("좌석 열 번호를 찾지 못했습니다. 좌석도 양끝 숫자가 범위에 포함되는지 확인하세요."); return; }
                    SeatChoice choice = SeatDetector.Choose(dots, attempted, maxSeats - clickedSeats);
                    if (choice == null) return;
                    foreach (SeatDot dot in choice.Group)
                    {
                        int x = mapRegion.Left + (int)Math.Round(dot.X);
                        int y = mapRegion.Top + (int)Math.Round(dot.Y);
                        NativeMouse.Click(x, y);
                        attempted.Add(new PointF(dot.X, dot.Y));
                        clickedSeats++;
                    }
                    timer.Stop(); running = false;
                    bool completed = ClickCompleteButton();
                    StopRun(completed
                        ? String.Format("{0}석을 선택하고 ‘선택 완료’를 눌렀습니다. 다음 화면을 확인하세요.", clickedSeats)
                        : String.Format("{0}석을 선택했습니다. ‘선택 완료’ 버튼을 찾지 못해 직접 눌러주세요.", clickedSeats));
                }
            }
            catch (Exception ex) { StopRun("오류로 중지했습니다: " + ex.Message); }
        }

        private bool ClickCompleteButton()
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                if (NativeWindows.GetForegroundWindow() != targetWindow) return false;
                Point center;
                if (NativeWindows.TryFindCompleteButton(targetWindow, out center))
                {
                    NativeMouse.Click(center.X, center.Y);
                    return true;
                }
                System.Threading.Thread.Sleep(25);
            }
            return false;
        }

        private void StopRun(string message)
        {
            timer.Stop(); running = false;
            if (Visible == false) { Show(); Activate(); }
            status.Text = message;
        }
    }
}
