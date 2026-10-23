using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PasteLoop
{
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct MouseInput
        { public int dx, dy; public uint mouseData, flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] internal struct KeyInput
        { public ushort key, scan; public uint flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] internal struct HardwareInput
        { public uint message; public ushort low, high; }
        [StructLayout(LayoutKind.Explicit)] internal struct InputUnion
        {
            [FieldOffset(0)] public MouseInput mouse;
            [FieldOffset(0)] public KeyInput keyboard;
            [FieldOffset(0)] public HardwareInput hardware;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Input
        { public uint type; public InputUnion data; }
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
        internal static Input Key(ushort key, bool up)
        {
            Input result = new Input(); result.type = 1;
            result.data.keyboard.key = key; result.data.keyboard.flags = up ? 2u : 0u;
            return result;
        }
        internal static bool ModifiersHeld()
        {
            foreach (int key in new int[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x01 })
                if ((GetAsyncKeyState(key) & 0x8000) != 0) return true;
            return false;
        }
        internal static Input[] PasteInputs(bool enterAfterPaste)
        {
            if (enterAfterPaste)
                return new Input[] { Key(0x11, false), Key(0x56, false), Key(0x56, true), Key(0x11, true), Key(0x0D, false), Key(0x0D, true) };
            return new Input[] { Key(0x11, false), Key(0x56, false), Key(0x56, true), Key(0x11, true) };
        }
        internal static Input[] ReleaseInputs(bool enterAfterPaste)
        {
            if (enterAfterPaste) return new Input[] { Key(0x56, true), Key(0x11, true), Key(0x0D, true) };
            return new Input[] { Key(0x56, true), Key(0x11, true) };
        }
        internal static bool Paste(bool enterAfterPaste)
        {
            Input[] keys = PasteInputs(enterAfterPaste);
            uint sent = SendInput((uint)keys.Length, keys, Marshal.SizeOf(typeof(Input)));
            if (sent == keys.Length) return true;
            // A partial sequence may leave V, Ctrl or Enter pressed.
            if (sent > 0)
            {
                Input[] releases = ReleaseInputs(enterAfterPaste);
                SendInput((uint)releases.Length, releases, Marshal.SizeOf(typeof(Input)));
            }
            return false;
        }
        internal static void Copy(string text)
        {
            var data = new System.Windows.Forms.DataObject(System.Windows.Forms.DataFormats.UnicodeText, text);
            System.Windows.Forms.Clipboard.SetDataObject(data, true, 2, 20);
        }
    }

    internal sealed class Controller
    {
        internal readonly Window Window;
        internal bool Running { get; private set; }
        internal long Count { get; private set; }
        internal bool Dark { get; private set; }
        internal bool StopHotkey { get; private set; }
        internal int StopHotkeyError { get; private set; }
        private bool startHotkey;
        private bool countdown;
        private bool enterAfterPaste;
        private readonly bool testing;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly DispatcherTimer timer;
        private long due;
        private int interval = 1000;
        private uint clipboardSequence;
        private IntPtr hwnd;
        private HwndSource source;
        private readonly TextBox text, intervalText;
        private readonly Button start;
        private readonly Button[] presets;
        private readonly string settingsPath;
        private readonly uint processId = (uint)Process.GetCurrentProcess().Id;

        internal Controller(bool testingMode)
        {
            testing = testingMode;
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PasteLoop.MainWindow.xaml"))
                Window = (Window)XamlReader.Load(stream);
            text = Find<TextBox>("PasteText"); intervalText = Find<TextBox>("IntervalText");
            start = Find<Button>("StartButton");
            settingsPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vkladac", "settings.txt");
            timer = new DispatcherTimer(DispatcherPriority.Input);
            timer.Interval = TimeSpan.FromMilliseconds(20);
            timer.Tick += delegate { Tick(); };
            presets = new Button[] { Find<Button>("PresetFast"), Find<Button>("PresetHalf"), Find<Button>("PresetNormal"), Find<Button>("PresetSlow") };
            foreach (Button preset in presets)
                preset.Click += delegate(object sender, RoutedEventArgs e) { SetInterval(double.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture)); };
            Find<Button>("LightButton").Click += delegate { ApplyTheme(false); Save(); };
            Find<Button>("DarkButton").Click += delegate { ApplyTheme(true); Save(); };
            Find<Button>("MinusButton").Click += delegate { Adjust(-0.1); };
            Find<Button>("PlusButton").Click += delegate { Adjust(0.1); };
            start.Click += delegate { Toggle(); };
            text.TextChanged += delegate
            {
                Find<TextBlock>("Placeholder").Visibility = text.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                Find<TextBlock>("CharacterCount").Text = text.Text.Length.ToString("N0", CultureInfo.InvariantCulture) + (text.Text.Length == 1 ? " character" : " characters");
            };
            intervalText.LostKeyboardFocus += delegate { ValidateInterval(); };
            intervalText.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == System.Windows.Input.Key.Enter) { ValidateInterval(); start.Focus(); e.Handled = true; }
            };
            Window.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            { if (e.Key == System.Windows.Input.Key.Escape && Running) { Stop("Stopped", "Pasting is stopped."); e.Handled = true; } };
            Window.SourceInitialized += delegate { Register(); };
            Window.Closed += delegate { Close(); };
            Load();
            CheckBox enterToggle = Find<CheckBox>("EnterToggle");
            enterToggle.IsChecked = enterAfterPaste;
            UpdateEnterState();
            RoutedEventHandler enterChanged = delegate
            {
                enterAfterPaste = enterToggle.IsChecked == true;
                UpdateEnterState(); Save();
            };
            enterToggle.Checked += enterChanged;
            enterToggle.Unchecked += enterChanged;
            ApplyTheme(Dark);
            SetInterval(interval / 1000.0);
        }

        internal T Find<T>(string name) where T : class { return Window.FindName(name) as T; }
        private void UpdateEnterState()
        { Find<TextBlock>("EnterState").Text = enterAfterPaste ? "On" : "Off"; }
        internal static bool ParseInterval(string value, out int milliseconds)
        {
            double seconds;
            bool valid = double.TryParse(value.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out seconds)
                && !double.IsNaN(seconds) && !double.IsInfinity(seconds) && seconds >= 0.1 && seconds <= 60;
            milliseconds = valid ? (int)Math.Round(seconds * 1000) : 1000;
            return valid;
        }
        private bool ValidateInterval()
        {
            int next;
            if (!ParseInterval(intervalText.Text, out next))
            {
                Status("Check the interval", "Enter a number between 0.1 and 60 seconds.", true);
                return false;
            }
            interval = next; SetInterval(interval / 1000.0); Save(); return true;
        }
        private void SetInterval(double seconds)
        {
            interval = (int)Math.Round(Math.Max(0.1, Math.Min(60, seconds)) * 1000);
            intervalText.Text = (interval / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
            UpdatePresetColors(); Save();
        }
        private void Adjust(double delta)
        {
            int current; if (ParseInterval(intervalText.Text, out current)) interval = current;
            SetInterval(interval / 1000.0 + delta);
        }
        private void UpdatePresetColors()
        {
            foreach (Button preset in presets)
            {
                bool selected = Math.Abs(double.Parse((string)preset.Tag, CultureInfo.InvariantCulture) * 1000 - interval) < 0.5;
                preset.Background = Brush(selected ? "AccentSoft" : "Input");
                preset.Foreground = Brush(selected ? "Accent" : "Muted");
                preset.BorderBrush = Brush(selected ? "Accent" : "Line");
            }
        }
        private SolidColorBrush Brush(string key) { return (SolidColorBrush)Window.Resources[key]; }
        internal void ApplyTheme(bool dark)
        {
            Dark = dark;
            string[] keys = { "Bg", "Card", "Text", "Muted", "Line", "Input", "Accent", "AccentSoft", "Danger" };
            string[] light = { "#F6F7F9", "#FFFFFF", "#202824", "#69746E", "#E2E8E3", "#F7F9F7", "#237854", "#E8F3EC", "#B5473B" };
            string[] night = { "#171B1A", "#202624", "#EEF3EF", "#A1B0A7", "#35413B", "#19201D", "#65BD91", "#2A4034", "#ED9C91" };
            for (int i = 0; i < keys.Length; i++) Window.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString((dark ? night : light)[i]));
            Find<Button>("LightButton").Background = Brush(dark ? "Card" : "AccentSoft");
            Find<Button>("LightButton").Foreground = Brush(dark ? "Muted" : "Accent");
            Find<Button>("DarkButton").Background = Brush(dark ? "AccentSoft" : "Card");
            Find<Button>("DarkButton").Foreground = Brush(dark ? "Accent" : "Muted");
            start.Background = Brush(Running ? "Danger" : "Accent");
            start.Foreground = dark ? Brush("Bg") : Brushes.White;
            Find<Ellipse>("StatusDot").Fill = Brush(Running ? "Accent" : "Muted");
            UpdatePresetColors();
        }
        private void Register()
        {
            hwnd = new WindowInteropHelper(Window).Handle;
            source = HwndSource.FromHwnd(hwnd); source.AddHook(Hook);
            startHotkey = Native.RegisterHotKey(hwnd, 1, 0x4000, 0x75);
            StopHotkey = Native.RegisterHotKey(hwnd, 2, 0x4000, 0x77);
            StopHotkeyError = StopHotkey ? 0 : Marshal.GetLastWin32Error();
            if (!startHotkey) Find<TextBlock>("HotkeyHint").Text = "Use the Start button · F6 is unavailable";
            if (!StopHotkey)
            {
                start.IsEnabled = false;
                Status("F8 is unavailable", "Close any app using F8, then reopen PasteLoop in a normal desktop session.", true);
            }
        }
        private IntPtr Hook(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x0312)
            {
                int id = wParam.ToInt32();
                if (id == 1) { Toggle(); handled = true; }
                if (id == 2) { if (Running) Stop("Stopped", "Stopped with F8."); handled = true; }
            }
            return IntPtr.Zero;
        }
        internal void Toggle()
        {
            if (Running) { Stop("Stopped", "Pasting is stopped."); return; }
            if (!StopHotkey) return;
            if (string.IsNullOrWhiteSpace(text.Text))
            { Status("Add your text first", "Enter the text you want to paste in the field above.", true); return; }
            if (!ValidateInterval()) return;
            try { Native.Copy(text.Text); clipboardSequence = Native.GetClipboardSequenceNumber(); }
            catch (ExternalException) { Status("Clipboard is busy", "Try starting again in a moment.", true); return; }
            Running = true; countdown = true; Count = 0;
            Find<TextBlock>("PasteCount").Text = "0";
            due = clock.ElapsedMilliseconds + 3000;
            SetEditing(false); start.Content = "Stop pasting";
            start.Background = Brush("Danger");
            Status("Starting in 3 s", "Switch to the target window and click a text field.", false);
            timer.Start();
        }
        private void SetEditing(bool enabled)
        {
            text.IsReadOnly = !enabled; intervalText.IsReadOnly = !enabled;
            Find<Button>("MinusButton").IsEnabled = enabled;
            Find<Button>("PlusButton").IsEnabled = enabled;
            foreach (Button preset in presets) preset.IsEnabled = enabled;
        }
        private void Tick()
        {
            if (!Running) return;
            if (Native.GetClipboardSequenceNumber() != clipboardSequence)
            { Stop("Clipboard changed", "Pasting stopped. Start again to copy your text back."); return; }
            long now = clock.ElapsedMilliseconds;
            if (countdown && now < due)
            {
                Status("Starting in " + ((due - now + 999) / 1000) + " s", "Switch to the target window and click a text field.", false);
                return;
            }
            countdown = false;
            IntPtr foreground = Native.GetForegroundWindow(); uint foregroundProcess;
            Native.GetWindowThreadProcessId(foreground, out foregroundProcess);
            if (foreground == IntPtr.Zero || foregroundProcess == processId)
            { Status("Waiting for a target window", "Switch to another app. Press F8 to stop at any time.", false); return; }
            if (Native.ModifiersHeld())
            { Status("Waiting for key release", "Release Ctrl, Alt, Shift, Win and the left mouse button.", false); return; }
            Status("Pasting", "Pasting into the active window. Press F8 to stop.", false);
            if (now < due) return;
            if (!Native.Paste(enterAfterPaste))
            { Stop("Paste failed", "The target may have higher permissions. Choose a regular text field."); return; }
            Count++; Find<TextBlock>("PasteCount").Text = Count.ToString("N0", CultureInfo.InvariantCulture);
            due = clock.ElapsedMilliseconds + interval;
        }
        internal void Stop(string title, string detail)
        {
            timer.Stop(); Running = false; countdown = false; SetEditing(true);
            start.Content = "Start pasting"; start.Background = Brush("Accent");
            Status(title, detail, false);
        }
        private void Status(string title, string detail, bool error)
        {
            Find<TextBlock>("StatusTitle").Text = title;
            Find<TextBlock>("StatusDetail").Text = detail;
            Find<Ellipse>("StatusDot").Fill = Brush(error ? "Danger" : Running ? "Accent" : "Muted");
        }
        private void Load()
        {
            if (testing) return;
            try
            {
                if (!File.Exists(settingsPath)) return;
                string[] lines = File.ReadAllLines(settingsPath);
                Dark = lines.Length > 0 && lines[0] == "dark";
                int stored;
                if (lines.Length > 1 && int.TryParse(lines[1], out stored) && stored >= 100 && stored <= 60000) interval = stored;
                enterAfterPaste = lines.Length > 2 && lines[2] == "enter";
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        private void Save()
        {
            if (testing) return;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(settingsPath));
                File.WriteAllLines(settingsPath, new string[] { Dark ? "dark" : "light", interval.ToString(CultureInfo.InvariantCulture), enterAfterPaste ? "enter" : "paste" });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        private void Close()
        {
            timer.Stop(); Running = false;
            if (startHotkey) Native.UnregisterHotKey(hwnd, 1);
            if (StopHotkey) Native.UnregisterHotKey(hwnd, 2);
            if (source != null) source.RemoveHook(Hook);
            Save();
        }
    }

    internal static class Program
    {
        [STAThread] public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--self-test") return SelfTest(args.Length > 1 ? args[1] : ".");
            if (args.Length > 0 && args[0] == "--verify") return Verify(args.Length > 1 ? args[1] : ".");
            bool first;
            using (var mutex = new Mutex(true, "Local\\Vkladac.Desktop.1", out first))
            {
                if (!first) { MessageBox.Show("PasteLoop or an earlier version is already running. Close its window before opening this version.", "PasteLoop"); return 0; }
                try
                {
                    var app = new Application(); var controller = new Controller(false);
                    app.Run(controller.Window);
                }
                catch (Exception)
                { MessageBox.Show("PasteLoop could not start. Check that .NET Framework 4.8 is enabled in Windows.", "PasteLoop"); return 1; }
            }
            return 0;
        }

        private static void Check(bool condition, string label)
        { if (!condition) throw new InvalidOperationException(label); }
        private static int Verify(string folder)
        {
            Directory.CreateDirectory(folder);
            Controller controller = null;
            try
            {
                Check(Marshal.SizeOf(typeof(Native.Input)) == (IntPtr.Size == 8 ? 40 : 28), "Native INPUT size");
                foreach (bool enter in new bool[] { false, true })
                {
                    Native.Input[] keys = Native.PasteInputs(enter);
                    Check(keys.Length == (enter ? 6 : 4), "Expected event count");
                    ushort[] codes = enter ? new ushort[] { 0x11, 0x56, 0x56, 0x11, 0x0D, 0x0D } : new ushort[] { 0x11, 0x56, 0x56, 0x11 };
                    uint[] flags = enter ? new uint[] { 0, 0, 2, 2, 0, 2 } : new uint[] { 0, 0, 2, 2 };
                    for (int i = 0; i < keys.Length; i++)
                        Check(keys[i].type == 1 && keys[i].data.keyboard.key == codes[i] && keys[i].data.keyboard.flags == flags[i], "Correct key order and release");
                    Native.Input[] cleanup = Native.ReleaseInputs(enter);
                    Check(cleanup.Length == (enter ? 3 : 2), "Release count");
                    for (int i = 0; i < cleanup.Length; i++) Check(cleanup[i].data.keyboard.flags == 2, "Cleanup releases keys");
                    if (enter) Check(cleanup[2].data.keyboard.key == 0x0D, "Cleanup releases Enter");
                }
                int ms;
                Check(Controller.ParseInterval("0,1", out ms) && ms == 100, "Minimum interval");
                Check(Controller.ParseInterval("60", out ms) && ms == 60000, "Maximum interval");
                Check(!Controller.ParseInterval("0", out ms), "Reject zero interval");
                var app = new Application();
                controller = new Controller(true);
                CheckBox toggle = controller.Find<CheckBox>("EnterToggle");
                Check(toggle.IsChecked == false && controller.Find<TextBlock>("EnterState").Text == "Off", "Enter defaults off");
                toggle.IsChecked = true;
                Check(controller.Find<TextBlock>("EnterState").Text == "On", "Enable Enter");
                toggle.IsChecked = false;
                Check(controller.Find<TextBlock>("EnterState").Text == "Off", "Disable Enter");
                controller.Find<TextBox>("PasteText").Text = "Earthworm";
                FrameworkElement content = (FrameworkElement)controller.Window.Content;
                foreach (bool dark in new bool[] { false, true })
                {
                    controller.ApplyTheme(dark);
                    toggle.IsChecked = dark;
                    content.Measure(new Size(584, 750));
                    content.Arrange(new Rect(0, 0, 584, 750)); content.UpdateLayout();
                    Check(toggle.ActualWidth > 0 && toggle.ActualHeight > 0, "Toggle has visible bounds");
                    var bitmap = new RenderTargetBitmap(584, 750, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var output = File.Create(System.IO.Path.Combine(folder, dark ? "enter-dark.png" : "enter-light.png"))) encoder.Save(output);
                }
                File.WriteAllText(System.IO.Path.Combine(folder, "verify.txt"), "PASS: native INPUT layout; paste-only sequence; Ctrl+V followed by plain Enter; cleanup releases Enter; interval bounds; default off; toggle on/off; both theme renders. No live desktop input was sent.");
                return 0;
            }
            catch (Exception ex)
            { File.WriteAllText(System.IO.Path.Combine(folder, "verify.txt"), ex.ToString()); return 1; }
            finally { if (controller != null) controller.Window.Close(); }
        }
        private static int SelfTest(string folder)
        {
            Directory.CreateDirectory(folder);
            try
            {
                Check(Marshal.SizeOf(typeof(Native.Input)) == (IntPtr.Size == 8 ? 40 : 28), "Native INPUT size");
                int ms;
                Check(Controller.ParseInterval("0,1", out ms) && ms == 100, "Comma decimal");
                Check(Controller.ParseInterval("0.25", out ms) && ms == 250, "Decimal precision");
                Check(Controller.ParseInterval("60", out ms) && ms == 60000, "Maximum interval");
                foreach (string invalid in new string[] { "0", "-1", "60.1", "NaN", "Infinity", "", "abc", "1,2,3" })
                    Check(!Controller.ParseInterval(invalid, out ms), "Reject invalid interval: " + invalid);
                var app = new Application();
                var controller = new Controller(true);
                controller.Find<TextBox>("PasteText").Text = "Earthworm";
                controller.Window.Show();
                Check(controller.StopHotkey, "F8 registered; native error=" + controller.StopHotkeyError);
                controller.Find<TextBox>("PasteText").Text = "";
                controller.Toggle(); Check(!controller.Running, "Empty text cannot start");
                controller.Find<TextBox>("PasteText").Text = "Earthworm\nUnicode text 🪱";
                controller.Toggle();
                Check(controller.Running, "Countdown starts");
                Check(System.Windows.Forms.Clipboard.GetText(System.Windows.Forms.TextDataFormat.UnicodeText) == controller.Find<TextBox>("PasteText").Text, "Unicode clipboard roundtrip");
                Check(controller.Count == 0, "Countdown does not paste immediately");
                controller.Stop("Stopped", "Pasting is stopped.");
                Check(!controller.Running && !controller.Find<TextBox>("PasteText").IsReadOnly, "Stop resets editing");
                controller.Find<TextBox>("PasteText").Text = "Earthworm";
                controller.Find<TextBlock>("StatusTitle").Text = "Ready";
                controller.Find<TextBlock>("StatusDetail").Text = "After starting, you have 3 seconds to switch windows.";
                foreach (bool dark in new bool[] { false, true })
                {
                    controller.ApplyTheme(dark); controller.Window.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)controller.Window.ActualWidth, (int)controller.Window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(controller.Window);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var output = File.Create(System.IO.Path.Combine(folder, dark ? "dark.png" : "light.png"))) encoder.Save(output);
                }
                controller.Window.Close();
                File.WriteAllText(System.IO.Path.Combine(folder, "self-test.txt"), "PASS: INPUT layout; interval bounds and decimal formats; F8 registration; blank-text rejection; countdown; Unicode clipboard; stop/editing; both theme renders.");
                return 0;
            }
            catch (Exception ex)
            { File.WriteAllText(System.IO.Path.Combine(folder, "self-test.txt"), ex.ToString()); return 1; }
        }
    }
}
