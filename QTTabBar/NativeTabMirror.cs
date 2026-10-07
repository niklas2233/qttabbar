using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using QTTabBarLib.Interop;
using Uia = System.Windows.Automation;

namespace QTTabBarLib {
    // Windows 11 draws Explorer's tab strip itself (WinUI), so a tab skin can't be applied to it. This
    // mirrors those native tabs into a QTabControl - one tab per native tab, same titles, icons, order
    // and selection - so QTTabBar's own drawing (including the skin) is what the user sees and clicks,
    // and forwards selecting, closing, adding and reordering back to Explorer.
    //
    // Explorer's UI Automation providers live on its UI thread, so every UIA call (and the ShellWindows
    // lookups used for icons) is made from a dedicated background thread; calling them from the UI
    // thread would wait on itself.
    internal sealed class NativeTabMirror {
        private enum ActionKind { Select, Close, Add, Reorder }

        private sealed class NativeTab {
            public string Key;
            public string Name;
            public string Path;      // folder path (or ::{CLSID}) used to pick the icon; null if unknown
            public bool Selected;
        }

        private sealed class Action {
            public ActionKind Kind;
            public string Key;
            public int ToIndex;
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

        // Dragging a tab on the bar (UI thread only).
        private int dragIndex = -1;
        private Point dragStart;
        private bool fDragging;
        // After a drop, Explorer needs a moment to reorder; until then (and while dragging) ignore native
        // snapshots and re-apply the latest one afterwards, so the bar neither jumps back nor misses a change.
        private DateTime holdUntil = DateTime.MinValue;
        private List<NativeTab> pending;
        private readonly System.Windows.Forms.Timer resync = new System.Windows.Forms.Timer { Interval = 400 };

        // One mirror per Explorer window thread; QTTabBarClass.RefreshOptions (which Options/Apply and
        // config broadcasts end up calling on that thread) uses this to reach it.
        [ThreadStatic]
        private static NativeTabMirror current;

        internal static void OnOptionsChanged() {
            if(current != null) current.RefreshOptions();
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

            resync.Tick += (sender, args) => {
                if(pending != null && !fDragging && DateTime.UtcNow >= holdUntil) {
                    List<NativeTab> latest = pending;
                    pending = null;
                    Apply(latest);
                }
            };
            resync.Start();

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

            tabs.MouseDown += (sender, e) => {
                dragIndex = -1;
                fDragging = false;
                if(e.Button != MouseButtons.Left) return;
                dragIndex = TabIndexAt(e.X);
                dragStart = e.Location;
            };
            tabs.MouseMove += (sender, e) => {
                if(dragIndex < 0 || (e.Button & MouseButtons.Left) == 0) return;
                if(!fDragging && Math.Abs(e.X - dragStart.X) < SystemInformation.DragSize.Width) return;
                fDragging = true;
                int target = TabIndexAt(e.X);
                if(target < 0) target = e.X < 0 ? 0 : shown.Count - 1;
                if(target != dragIndex) {
                    MoveLocally(dragIndex, target);
                    dragIndex = target;
                }
            };
            tabs.MouseUp += (sender, e) => {
                if(fDragging && dragIndex >= 0 && dragIndex < shown.Count) {
                    actions.Enqueue(new Action { Kind = ActionKind.Reorder, Key = shown[dragIndex].Key, ToIndex = dragIndex });
                    holdUntil = DateTime.UtcNow.AddMilliseconds(1800);
                }
                fDragging = false;
                dragIndex = -1;
            };

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

        // Move a tab within the bar (and our own list, kept in the bar's order) while dragging.
        private void MoveLocally(int from, int to) {
            fApplying = true;
            try {
                NativeTab tab = shown[from];
                shown.RemoveAt(from);
                shown.Insert(to, tab);
                tabs.TabPages.Relocate(from, to);
                tabs.Refresh();
            }
            finally {
                fApplying = false;
            }
        }

        private void Apply(List<NativeTab> now) {
            if(tabs.IsDisposed) return;
            if(fDragging || DateTime.UtcNow < holdUntil) {
                pending = now;
                return;
            }
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
                Uia.AutomationElement first = Uia.AutomationElement.FromHandle(hwndFrame).FindFirst(Uia.TreeScope.Descendants, TabItemCondition);
                if(first == null) return null;
                tabStrip = Uia.TreeWalker.ControlViewWalker.GetParent(first);
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
                case ActionKind.Reorder:
                    ReorderNative(action.Key, action.ToIndex);
                    break;
            }
        }

        // Explorer offers no API to move a tab, only dragging, so perform the drag it expects: press on the
        // tab, move across the strip in small steps (the XAML tab view needs real pointer movement), release
        // over the tab that currently holds the destination slot, then put the cursor back where it was.
        private void ReorderNative(string key, int toIndex) {
            List<Uia.AutomationElement> items = ReadTabItems();
            if(items == null) return;
            int from = items.FindIndex(item => KeyOf(item) == key);
            QTUtility2.flog("NativeTabMirror reorder: from=" + from + " to=" + toIndex + " tabs=" + items.Count);
            if(from < 0 || toIndex < 0 || toIndex >= items.Count || from == toIndex) return;

            System.Windows.Rect src = items[from].Current.BoundingRectangle;
            System.Windows.Rect dst = items[toIndex].Current.BoundingRectangle;
            if(src.IsEmpty || dst.IsEmpty) return;
            int sx = (int)(src.X + src.Width / 2), sy = (int)(src.Y + src.Height / 2);
            int tx = (int)(dst.X + dst.Width / 2);

            Point saved = Cursor.Position;
            try {
                NativeInput.SetCursorPos(sx, sy);
                Thread.Sleep(40);
                NativeInput.mouse_event(NativeInput.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(80);
                const int steps = 16;
                for(int i = 1; i <= steps; i++) {
                    NativeInput.SetCursorPos(sx + (tx - sx) * i / steps, sy);
                    Thread.Sleep(20);
                }
                Thread.Sleep(80);
            }
            finally {
                NativeInput.mouse_event(NativeInput.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(60);
                NativeInput.SetCursorPos(saved.X, saved.Y);
            }
        }

        private static class NativeInput {
            public const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
            [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
            [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
        }
    }
}
