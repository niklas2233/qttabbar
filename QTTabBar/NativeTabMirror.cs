using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using QTTabBarLib.Interop;
using Uia = System.Windows.Automation;

namespace QTTabBarLib {
    // Windows 11 draws Explorer's tab strip itself (WinUI), so a tab skin can't be applied to it. This
    // mirrors those native tabs into a QTabControl - one tab per native tab, same titles, same selection -
    // so QTTabBar's own drawing (including the skin) is what the user sees and clicks, and forwards
    // selecting, closing and adding back to Explorer through UI Automation.
    //
    // Explorer's UI Automation providers live on its UI thread, so every UIA call is made from a
    // dedicated background thread (calling them from the UI thread would wait on itself).
    internal sealed class NativeTabMirror {
        private enum ActionKind { Select, Close, Add }

        private sealed class NativeTab {
            public string Key;
            public string Name;
            public bool Selected;
        }

        private sealed class Action {
            public ActionKind Kind;
            public string Key;
        }

        private static readonly Uia.Condition TabItemCondition =
                new Uia.PropertyCondition(Uia.AutomationElement.ControlTypeProperty, Uia.ControlType.TabItem);

        private readonly QTabControl tabs;
        private readonly IntPtr hwndFrame;
        private readonly ConcurrentQueue<Action> actions = new ConcurrentQueue<Action>();
        private List<NativeTab> shown = new List<NativeTab>();   // UI thread only
        private bool fApplying;                                  // UI thread only

        public NativeTabMirror(IntPtr hwndFrame) {
            this.hwndFrame = hwndFrame;
            tabs = new QTabControl();
            tabs.RefreshOptions(true);
            // Same dark/light backgrounds Explorer's own content area uses.
            tabs.BackColor = QTUtility.getNightMode() ? System.Drawing.Color.FromArgb(32, 32, 32) : System.Drawing.Color.FromArgb(243, 243, 243);
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
            get { return tabs; }
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
            if(tabStrip == null) {
                Uia.AutomationElement first = Uia.AutomationElement.FromHandle(hwndFrame).FindFirst(Uia.TreeScope.Descendants, TabItemCondition);
                if(first == null) return null;
                tabStrip = Uia.TreeWalker.ControlViewWalker.GetParent(first);
                if(tabStrip == null) return null;
            }
            Uia.AutomationElementCollection items;
            try {
                items = tabStrip.FindAll(Uia.TreeScope.Children, TabItemCondition);
            }
            catch(Uia.ElementNotAvailableException) {
                tabStrip = null;
                return null;
            }
            List<NativeTab> list = new List<NativeTab>();
            foreach(Uia.AutomationElement item in items) {
                bool selected = false;
                object pattern;
                if(item.TryGetCurrentPattern(Uia.SelectionItemPattern.Pattern, out pattern)) {
                    selected = ((Uia.SelectionItemPattern)pattern).Current.IsSelected;
                }
                list.Add(new NativeTab { Key = string.Join(".", item.GetRuntimeId()), Name = item.Current.Name, Selected = selected });
            }
            return list;
        }

        private Uia.AutomationElement FindTab(string key) {
            if(tabStrip == null) return null;
            foreach(Uia.AutomationElement item in tabStrip.FindAll(Uia.TreeScope.Children, TabItemCondition)) {
                if(string.Join(".", item.GetRuntimeId()) == key) return item;
            }
            return null;
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

        // ---- UI thread -------------------------------------------------------------------------

        private void Apply(List<NativeTab> now) {
            if(tabs.IsDisposed) return;
            fApplying = true;
            try {
                bool sameTabs = shown.Count == now.Count && shown.Select(t => t.Key).SequenceEqual(now.Select(t => t.Key));
                if(!sameTabs) {
                    while(tabs.TabPages.Count > 0) {
                        tabs.TabPages.Remove(tabs.TabPages[tabs.TabPages.Count - 1]);
                    }
                    foreach(NativeTab tab in now) {
                        tabs.TabPages.Add(new QTabItem(tab.Name, string.Empty, tabs));
                    }
                }
                else {
                    for(int i = 0; i < now.Count; i++) {
                        if(shown[i].Name != now[i].Name) {
                            tabs.TabPages[i].Text = now[i].Name;
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
    }
}
