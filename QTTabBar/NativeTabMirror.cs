using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using QTTabBarLib.Interop;
using Uia = System.Windows.Automation;

namespace QTTabBarLib {
    // Windows 11 draws Explorer's tab strip itself (WinUI), so a tab skin can't be applied to it. This
    // mirrors those native tabs into a QTabControl - one tab per native tab, same titles, icons, order
    // and selection - so QTTabBar's own drawing (including the skin) is what the user sees and clicks,
    // and forwards selecting, closing and adding back to Explorer. Order only ever flows from Explorer to
    // the bar: Explorer has no API to move a tab (only dragging), so the bar doesn't reorder tabs itself.
    //
    // Explorer's UI Automation providers live on its UI thread, so every UIA call (and the ShellWindows
    // lookups used for icons) is made from a dedicated background thread; calling them from the UI
    // thread would wait on itself.
    internal sealed class NativeTabMirror {
        private enum ActionKind { Select, Close, Add }

        private sealed class NativeTab {
            public string Key;
            public string Name;
            public string Path;      // folder path (or ::{CLSID}) used to pick the icon; null if unknown
            public bool Selected;
        }

        private sealed class Action {
            public ActionKind Kind;
            public string Key;
        }

        private static readonly Uia.Condition TabItemCondition =
                new Uia.PropertyCondition(Uia.AutomationElement.ControlTypeProperty, Uia.ControlType.TabItem);

        private const string HomePath = "::{F874310E-B6B7-47DC-BC84-B9E6B38F5903}";

        private readonly QTabControl tabs;
        // QTabControl expects a managed Parent (it forwards WM_CONTEXTMENU to Parent.Handle and uses
        // Parent.RectangleToScreen while right-dragging), so it lives inside this container, which is
        // what actually gets parented into Explorer's window.
        private readonly BarContainer container;

        // Catches the WM_CONTEXTMENU QTabControl forwards to its parent, so the menu is ours rather
        // than Explorer's (DefWindowProc would pass it on to the Explorer frame).
        private sealed class BarContainer : Panel {
            public event System.Action<Point> ContextMenuRequested;

            protected override void WndProc(ref Message m) {
                if(m.Msg == 0x7B) {   // WM_CONTEXTMENU: lParam is the screen position, or -1 for the keyboard
                    int lp = unchecked((int)m.LParam.ToInt64());
                    Point pt = lp == -1 ? Cursor.Position : new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                    if(ContextMenuRequested != null) ContextMenuRequested(pt);
                    return;
                }
                base.WndProc(ref m);
            }
        }
        private readonly IntPtr hwndFrame;
        private readonly ConcurrentQueue<Action> actions = new ConcurrentQueue<Action>();
        private List<NativeTab> shown = new List<NativeTab>();   // UI thread only; always in the bar's order
        private bool fApplying;                                  // UI thread only
        private FileSystemWatcher skinWatcher;
        private readonly System.Windows.Forms.Timer skinDebounce = new System.Windows.Forms.Timer { Interval = 300 };


        // One mirror per Explorer window thread; QTTabBarClass.RefreshOptions (which Options/Apply and
        // config broadcasts end up calling on that thread) uses this to reach it.
        [ThreadStatic]
        private static NativeTabMirror current;

        private Win11BarHost host;
        private volatile bool enabled = true;
        private volatile bool fResend;

        // Called on a window's UI thread when it attaches and whenever options change: creates, shows or
        // hides the bar so the setting takes effect in windows that are already open, without a restart.
        internal static void Sync(IntPtr hwndFrame) {
            WatchSetting(hwndFrame);
            bool want = Config.Window.ShowTabBar && Win11BarHost.HasNativeTabs(hwndFrame);
            if(want) {
                if(current == null) {
                    NativeTabMirror mirror = new NativeTabMirror(hwndFrame);   // also registers itself as current
                    mirror.host = new Win11BarHost(mirror.Bar, hwndFrame);
                }
                else {
                    current.SetEnabled(true);
                }
            }
            else if(current != null) {
                current.SetEnabled(false);
            }
        }

        // The setting is changed in the Options dialog, which runs in another Explorer process, and the
        // notification that is supposed to reach every window's process doesn't do so reliably (the
        // inter-process broadcast regularly fails, and windows in the dialog's own process were missed).
        // So each window also checks the saved value itself and follows it, which is cheap and always works.
        [ThreadStatic]
        private static System.Windows.Forms.Timer settingWatch;

        [ThreadStatic]
        private static string lastConfigSignature;

        // A cheap fingerprint of the saved settings that affect the bar (skin image, colours, tab sizes,
        // window options), used to notice that Options changed something.
        private static string ConfigSignature() {
            StringBuilder sb = new StringBuilder();
            foreach(string category in new[] { "Skin", "Tabs", "Window" }) {
                using(Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\QTTabBar\Config\" + category)) {
                    if(key == null) continue;
                    foreach(string name in key.GetValueNames().OrderBy(n => n, StringComparer.Ordinal)) {
                        sb.Append(category).Append('.').Append(name).Append('=').Append(key.GetValue(name)).Append(';');
                    }
                }
            }
            return sb.ToString();
        }

        private static void WatchSetting(IntPtr hwndFrame) {
            if(settingWatch != null) return;
            settingWatch = new System.Windows.Forms.Timer { Interval = 1000 };
            settingWatch.Tick += (sender, args) => {
                if(!PInvoke.IsWindow(hwndFrame)) {
                    settingWatch.Stop();
                    return;
                }
                // Other appearance changes (skin image, colours, sizes) reach the bar through the same
                // unreliable notification, so reload the saved settings and refresh when they change.
                string signature = ConfigSignature();
                if(lastConfigSignature != null && signature != lastConfigSignature) {
                    ConfigManager.ReadConfig();
                    if(current != null && current.enabled) current.RefreshOptions();
                }
                lastConfigSignature = signature;
                // Compare with what this window is actually showing, not with Config: in the process that
                // hosts the Options dialog the in-memory setting is already updated, yet its windows never
                // heard about it.
                object saved = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\QTTabBar\Config\Window", "ShowTabBar", null);
                bool want = saved is int ? (int)saved != 0 : Config.Window.ShowTabBar;
                bool showing = current != null && current.enabled;
                if(want != showing && (!want || Win11BarHost.HasNativeTabs(hwndFrame))) {
                    Config.Window.ShowTabBar = want;
                    Sync(hwndFrame);
                }
            };
            settingWatch.Start();
        }

        private void SetEnabled(bool on) {
            if(enabled == on || host == null) return;
            enabled = on;
            if(on) {
                fResend = true;   // tabs may have changed while hidden
                host.Enable();
            }
            else {
                host.Disable();
            }
        }

        internal static void OnOptionsChanged(IntPtr hwndFrame) {
            Sync(hwndFrame);
            if(current != null && current.enabled) current.RefreshOptions();
        }

        public NativeTabMirror(IntPtr hwndFrame) {
            this.hwndFrame = hwndFrame;
            tabs = new QTabControl();
            container = new BarContainer { Margin = Padding.Empty, Padding = Padding.Empty };
            container.ContextMenuRequested += ShowContextMenu;
            tabs.Dock = DockStyle.Fill;
            container.Controls.Add(tabs);
            tabs.RefreshOptions(true);
            ApplyBackColor();
            current = this;
            skinDebounce.Tick += (sender, args) => {
                skinDebounce.Stop();
                RefreshOptions();
            };
            WatchSkinFile();

            tabs.SelectedIndexChanged += (sender, args) => {
                if(fApplying || tabs.SelectedIndex < 0 || tabs.SelectedIndex >= shown.Count) return;
                actions.Enqueue(new Action { Kind = ActionKind.Select, Key = shown[tabs.SelectedIndex].Key });
            };
            tabs.CloseButtonClicked += (sender, e) => {
                if(e.TabPageIndex >= 0 && e.TabPageIndex < shown.Count) {
                    actions.Enqueue(new Action { Kind = ActionKind.Close, Key = shown[e.TabPageIndex].Key });
                }
            };
            tabs.PlusButtonClicked += (sender, e) => actions.Enqueue(new Action { Kind = ActionKind.Add });

            Thread poller = new Thread(PollLoop) { IsBackground = true, Name = "QTTabBar native tab mirror" };
            poller.SetApartmentState(ApartmentState.MTA);
            poller.Start();
        }

        public Control Bar {
            get { return container; }
        }

        // Same dark/light backgrounds Explorer's own content area uses.
        private void ApplyBackColor() {
            tabs.BackColor = container.BackColor = QTUtility.getNightMode() ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243);
        }

        private void RefreshOptions() {
            if(tabs.IsDisposed) return;
            QTUtility2.flog("NativeTabMirror RefreshOptions: skin=" + Config.Skin.UseTabSkin + " file=" + Config.Skin.TabImageFile);
            tabs.RefreshOptions(false);
            ApplyBackColor();
            tabs.Refresh();
            WatchSkinFile();
        }

        // Editing the skin PNG in place doesn't go through Options, so watch the file itself.
        private void WatchSkinFile() {
            if(skinWatcher != null) {
                skinWatcher.EnableRaisingEvents = false;
                skinWatcher.Dispose();
                skinWatcher = null;
            }
            string file = Config.Skin.UseTabSkin ? Config.Skin.TabImageFile : null;
            try {
                if(string.IsNullOrEmpty(file) || !File.Exists(file)) return;
                skinWatcher = new FileSystemWatcher(System.IO.Path.GetDirectoryName(file), System.IO.Path.GetFileName(file)) {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
                };
                FileSystemEventHandler changed = (sender, e) => {
                    try {
                        // Editors save in several bursts; refresh once things have settled.
                        if(tabs.IsHandleCreated && !tabs.IsDisposed) {
                            tabs.BeginInvoke(new MethodInvoker(() => { skinDebounce.Stop(); skinDebounce.Start(); }));
                        }
                    }
                    catch(InvalidOperationException) {
                    }
                };
                skinWatcher.Changed += changed;
                skinWatcher.Created += changed;
                skinWatcher.Renamed += (sender, e) => changed(sender, e);
                skinWatcher.EnableRaisingEvents = true;
            }
            catch(Exception e) {
                QTUtility2.log("NativeTabMirror WatchSkinFile: " + e.Message);
            }
        }

        // ---- UI thread -------------------------------------------------------------------------

        private int TabIndexAt(int x) {
            for(int i = 0; i < tabs.TabPages.Count; i++) {
                Rectangle bounds = tabs.TabPages[i].TabBounds;
                if(x >= bounds.Left && x < bounds.Right) return i;
            }
            return -1;
        }

        private void ShowContextMenu(Point screen) {
            int index = TabIndexAt(tabs.PointToClient(screen).X);
            QTUtility2.flog("NativeTabMirror context menu at " + screen + " tab index=" + index + " of " + shown.Count);
            ContextMenuStripEx menu = new ContextMenuStripEx(null, false);
            menu.Items.Add("New tab", null, (sender, e) => actions.Enqueue(new Action { Kind = ActionKind.Add }));
            if(index >= 0 && index < shown.Count) {
                string key = shown[index].Key;
                List<string> others = shown.Where(t => t.Key != key).Select(t => t.Key).ToList();
                List<string> right = shown.Skip(index + 1).Select(t => t.Key).ToList();
                menu.Items.Add("Close tab", null, (sender, e) => CloseTabs(new List<string> { key }));
                if(others.Count > 0) menu.Items.Add("Close other tabs", null, (sender, e) => CloseTabs(others));
                if(right.Count > 0) menu.Items.Add("Close tabs to the right", null, (sender, e) => CloseTabs(right));
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("QTTabBar Options...", null, (sender, e) => OptionsDialog.Open());
            // Dispose after the click has been handled, not from inside Closed.
            menu.Closed += (sender, e) => {
                if(tabs.IsHandleCreated && !tabs.IsDisposed) tabs.BeginInvoke(new MethodInvoker(menu.Dispose));
            };
            menu.Show(screen);
        }

        private void CloseTabs(IEnumerable<string> keys) {
            foreach(string key in keys) actions.Enqueue(new Action { Kind = ActionKind.Close, Key = key });
        }

        private void Apply(List<NativeTab> now) {
            if(tabs.IsDisposed) return;
            fApplying = true;
            try {
                bool sameTabs = shown.Count == now.Count && shown.Select(t => t.Key).SequenceEqual(now.Select(t => t.Key));
                if(!sameTabs) {
                    QTUtility2.flog("NativeTabMirror rebuild: " + string.Join(" | ", shown.Select(t => t.Name)) + "  ->  " + string.Join(" | ", now.Select(t => t.Name)));
                    while(tabs.TabPages.Count > 0) {
                        tabs.TabPages.Remove(tabs.TabPages[tabs.TabPages.Count - 1]);
                    }
                    foreach(NativeTab tab in now) {
                        tabs.TabPages.Add(NewItem(tab));
                    }
                }
                else {
                    for(int i = 0; i < now.Count; i++) {
                        if(shown[i].Name != now[i].Name) {
                            tabs.TabPages[i].Text = now[i].Name;
                        }
                        if(shown[i].Path != now[i].Path && now[i].Path != null) {
                            tabs.TabPages[i].ImageKey = now[i].Path;
                        }
                    }
                }
                int selected = now.FindIndex(t => t.Selected);
                if(selected >= 0 && tabs.SelectedIndex != selected) {
                    tabs.SelectedIndex = selected;
                }
                shown = now;
                tabs.Refresh();
            }
            finally {
                fApplying = false;
            }
        }

        private QTabItem NewItem(NativeTab tab) {
            QTabItem item = new QTabItem(tab.Name, tab.Path ?? string.Empty, tabs);
            if(tab.Path != null) item.ImageKey = tab.Path;
            return item;
        }

        // ---- background thread -------------------------------------------------------------------

        private Uia.AutomationElement tabStrip;
        private Uia.AutomationElement addButton;

        private void PollLoop() {
            List<string> lastSent = null;
            int tick = 0;
            while(PInvoke.IsWindow(hwndFrame)) {
                if(!enabled) {
                    Thread.Sleep(200);
                    continue;
                }
                if(fResend) {
                    fResend = false;
                    lastSent = null;
                }
                try {
                    Action action;
                    bool fActed = false;
                    while(actions.TryDequeue(out action)) {
                        Perform(action);
                        fActed = true;
                    }
                    // Poll every 250ms, or at once after we've changed something.
                    if(fActed || ++tick % 5 == 0) {
                        List<NativeTab> now = ReadTabs();
                        if(now != null) {
                            List<string> signature = now.Select(t => t.Key + "|" + t.Name + "|" + t.Selected).ToList();
                            if(lastSent == null || !signature.SequenceEqual(lastSent)) {
                                // Locations (for icons) only matter when tabs or titles changed.
                                AssignPaths(now);
                                lastSent = signature;
                                Post(now);
                            }
                        }
                    }
                }
                catch(Exception e) {
                    // Explorer's UI tree changes under us (tabs closing, window closing); start over next tick.
                    tabStrip = null;
                    addButton = null;
                    QTUtility2.log("NativeTabMirror poll: " + e.Message);
                }
                Thread.Sleep(50);
            }
        }

        private void Post(List<NativeTab> now) {
            try {
                if(tabs.IsHandleCreated && !tabs.IsDisposed) tabs.BeginInvoke(new MethodInvoker(() => Apply(now)));
            }
            catch(InvalidOperationException) {
                // bar is being torn down
            }
        }

        private List<NativeTab> ReadTabs() {
            List<Uia.AutomationElement> items = ReadTabItems();
            if(items == null) return null;
            List<NativeTab> list = new List<NativeTab>();
            foreach(Uia.AutomationElement item in items) {
                bool selected = false;
                object pattern;
                if(item.TryGetCurrentPattern(Uia.SelectionItemPattern.Pattern, out pattern)) {
                    selected = ((Uia.SelectionItemPattern)pattern).Current.IsSelected;
                }
                list.Add(new NativeTab { Key = KeyOf(item), Name = item.Current.Name, Selected = selected });
            }
            return list;
        }

        private static string KeyOf(Uia.AutomationElement item) {
            return string.Join(".", item.GetRuntimeId());
        }

        private List<Uia.AutomationElement> ReadTabItems() {
            if(tabStrip == null) {
                // Other tab-like items exist in the window (the Home page's Recent/Favourites pills are
                // TabItems too), but only the native tabs carry a close button, so locate the strip from that.
                Uia.AutomationElement close = Uia.AutomationElement.FromHandle(hwndFrame).FindFirst(Uia.TreeScope.Descendants,
                        new Uia.PropertyCondition(Uia.AutomationElement.AutomationIdProperty, "CloseButton"));
                if(close == null) return null;
                Uia.AutomationElement tab = Uia.TreeWalker.ControlViewWalker.GetParent(close);
                tabStrip = tab == null ? null : Uia.TreeWalker.ControlViewWalker.GetParent(tab);
                if(tabStrip == null) return null;
            }
            try {
                return tabStrip.FindAll(Uia.TreeScope.Children, TabItemCondition).Cast<Uia.AutomationElement>().ToList();
            }
            catch(Uia.ElementNotAvailableException) {
                tabStrip = null;
                return null;
            }
        }

        // Each native tab is its own ShellWindows entry (same frame HWND) with the tab's title as its
        // location name; pair them up by title. Duplicate titles are paired in order, which can only
        // mix up icons between two same-named tabs.
        private void AssignPaths(List<NativeTab> tabsNow) {
            List<KeyValuePair<string, string>> pool = new List<KeyValuePair<string, string>>();
            SHDocVw.ShellWindows windows = null;
            try {
                windows = new SHDocVw.ShellWindows();
                foreach(object o in windows) {
                    try {
                        SHDocVw.IWebBrowser2 wb = o as SHDocVw.IWebBrowser2;
                        if(wb == null || (IntPtr)wb.HWND != hwndFrame) continue;
                        pool.Add(new KeyValuePair<string, string>(wb.LocationName, wb.LocationURL));
                    }
                    catch(COMException) {
                        // that window went away mid-enumeration
                    }
                }
            }
            catch(Exception e) {
                QTUtility2.log("NativeTabMirror AssignPaths: " + e.Message);
            }
            finally {
                if(windows != null) Marshal.ReleaseComObject(windows);
            }
            foreach(NativeTab tab in tabsNow) {
                int index = pool.FindIndex(p => p.Key == tab.Name);
                if(index < 0) continue;
                tab.Path = PathFromUrl(pool[index].Value);
                pool.RemoveAt(index);
            }
        }

        private static string PathFromUrl(string url) {
            if(string.IsNullOrEmpty(url)) return HomePath;   // Home has no URL
            try {
                if(url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return new Uri(url).LocalPath;
                if(url.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase)) return url.Substring("shell:".Length);
                if(url.StartsWith("::")) return url;
            }
            catch(UriFormatException) {
            }
            return null;
        }

        private Uia.AutomationElement FindTab(string key) {
            List<Uia.AutomationElement> items = ReadTabItems();
            return items == null ? null : items.FirstOrDefault(item => KeyOf(item) == key);
        }

        private void Perform(Action action) {
            switch(action.Kind) {
                case ActionKind.Select: {
                    Uia.AutomationElement item = FindTab(action.Key);
                    object pattern;
                    if(item != null && item.TryGetCurrentPattern(Uia.SelectionItemPattern.Pattern, out pattern)) {
                        ((Uia.SelectionItemPattern)pattern).Select();
                    }
                    break;
                }
                case ActionKind.Close: {
                    Uia.AutomationElement item = FindTab(action.Key);
                    if(item == null) break;
                    Uia.AutomationElement close = item.FindFirst(Uia.TreeScope.Descendants,
                            new Uia.PropertyCondition(Uia.AutomationElement.AutomationIdProperty, "CloseButton"));
                    object pattern;
                    if(close != null && close.TryGetCurrentPattern(Uia.InvokePattern.Pattern, out pattern)) {
                        ((Uia.InvokePattern)pattern).Invoke();
                    }
                    break;
                }
                case ActionKind.Add: {
                    if(addButton == null) {
                        addButton = Uia.AutomationElement.FromHandle(hwndFrame).FindFirst(Uia.TreeScope.Descendants,
                                new Uia.PropertyCondition(Uia.AutomationElement.AutomationIdProperty, "AddButton"));
                    }
                    object pattern;
                    if(addButton != null && addButton.TryGetCurrentPattern(Uia.InvokePattern.Pattern, out pattern)) {
                        ((Uia.InvokePattern)pattern).Invoke();
                    }
                    break;
                }
            }
        }
    }
}
