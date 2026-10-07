using System;
using System.Collections.Generic;
using System.Windows.Forms;
using QTTabBarLib.Interop;

namespace QTTabBarLib {
    // Windows 11's Explorer has no rebar for QTTabBar's band to dock into: the header (tabs, address
    // bar, command bar) is a single WinUI DesktopChildSiteBridge, so the tab bar would otherwise
    // exist but never be drawn anywhere. This parents the bar into the Explorer frame, directly under
    // that header, and shifts the per-tab ShellTabWindowClass containers down to leave it room.
    // Layout is idempotent and re-applied on a short timer, so it follows whatever Explorer does on
    // resize, DPI change or tab switch.
    internal sealed class Win11BarHost {
        private const string BridgeClass = "Microsoft.UI.Content.DesktopChildSiteBridge";
        private const int WS_POPUP = unchecked((int)0x80000000), WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000,
                WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;
        private const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        private static readonly IntPtr HWND_TOP = IntPtr.Zero;

        // Keeps each host (and so its timer) alive for as long as its Explorer window exists.
        private static readonly List<Win11BarHost> alive = new List<Win11BarHost>();

        private readonly Control bar;
        private readonly IntPtr hwndFrame;
        private readonly Timer timer = new Timer { Interval = 200 };
        private bool fParented;
        private int appliedHeight;   // how far the file views are currently pushed down; 0 = not shifted
        private string lastState;

        // Logs only when the state changes, so the 200ms timer doesn't flood the log.
        private void State(string state) {
            if(state == lastState) return;
            lastState = state;
            QTUtility2.flog("Win11BarHost frame=" + hwndFrame + " " + state);
        }

        public Win11BarHost(Control bar, IntPtr hwndFrame) {
            this.bar = bar;
            this.hwndFrame = hwndFrame;
            lock(alive) alive.Add(this);
            State("created");
            timer.Tick += (sender, args) => Layout();
            timer.Start();
            Layout();
        }

        private void Stop() {
            timer.Stop();
            lock(alive) alive.Remove(this);
        }

        // Only Explorer with native (WinUI) tabs has this header; the classic layout has a rebar that
        // QTTabBar's own band docks into, so there is nothing to host or mirror there.
        public static bool HasNativeTabs(IntPtr hwndFrame) {
            return PInvoke.FindWindowEx(hwndFrame, IntPtr.Zero, BridgeClass, null) != IntPtr.Zero;
        }

        // Hide the bar and give the file views their space back (the setting was turned off).
        public void Disable() {
            timer.Stop();
            try {
                if(!PInvoke.IsWindow(hwndFrame)) return;
                IntPtr hwndBridge = PInvoke.FindWindowEx(hwndFrame, IntPtr.Zero, BridgeClass, null);
                if(hwndBridge != IntPtr.Zero && appliedHeight > 0) {
                    int barTop = RectInFrame(hwndBridge, hwndFrame).bottom;
                    foreach(IntPtr container in Containers()) {
                        RECT rc = RectInFrame(container, hwndFrame);
                        if(rc.top == barTop + appliedHeight) {
                            PInvoke.SetWindowPos(container, IntPtr.Zero, rc.left, barTop, rc.right - rc.left,
                                    rc.bottom - rc.top + appliedHeight, SWP_NOZORDER | SWP_NOACTIVATE);
                        }
                    }
                }
                appliedHeight = 0;
                if(fParented && bar.IsHandleCreated) PInvoke.ShowWindow(bar.Handle, 0);
                State("disabled");
            }
            catch(Exception e) {
                QTUtility2.MakeErrorLog(e, "Win11BarHost.Disable");
            }
        }

        public void Enable() {
            if(!PInvoke.IsWindow(hwndFrame)) return;
            timer.Start();
            Layout();
        }

        private List<IntPtr> Containers() {
            List<IntPtr> containers = new List<IntPtr>();
            IntPtr hwnd = IntPtr.Zero;
            while((hwnd = PInvoke.FindWindowEx(hwndFrame, hwnd, "ShellTabWindowClass", null)) != IntPtr.Zero) {
                containers.Add(hwnd);
            }
            return containers;
        }

        private static RECT RectInFrame(IntPtr hwnd, IntPtr hwndFrame) {
            RECT rc;
            PInvoke.GetWindowRect(hwnd, out rc);
            PInvoke.MapWindowPoints(IntPtr.Zero, hwndFrame, ref rc, 2);
            return rc;
        }

        private void Layout() {
            try {
                if(!PInvoke.IsWindow(hwndFrame)) {
                    Stop();
                    return;
                }
                if(!PInvoke.IsWindowVisible(hwndFrame)) { State("frame not visible"); return; }
                IntPtr hwndBridge = PInvoke.FindWindowEx(hwndFrame, IntPtr.Zero, BridgeClass, null);
                IntPtr hwndActive = WindowUtils.GetShellTabWindowClass(hwndFrame);
                if(hwndBridge == IntPtr.Zero || hwndActive == IntPtr.Zero) { State("no header bridge or container: bridge=" + hwndBridge + " container=" + hwndActive); return; }

                int height = Math.Max(24, Config.Skin.TabHeight + 2);
                int barTop = RectInFrame(hwndBridge, hwndFrame).bottom;
                RECT active = RectInFrame(hwndActive, hwndFrame);

                // Every native tab has its own container; shift each one that Explorer has put
                // directly under the header (inactive ones are repositioned on the next switch).
                foreach(IntPtr container in Containers()) {
                    RECT rc = RectInFrame(container, hwndFrame);
                    if(appliedHeight > 0 && rc.top == barTop + appliedHeight) {
                        // Pushed down by an earlier layout; follow a changed bar height.
                        if(appliedHeight != height) {
                            PInvoke.SetWindowPos(container, IntPtr.Zero, rc.left, barTop + height, rc.right - rc.left,
                                    rc.bottom - rc.top + appliedHeight - height, SWP_NOZORDER | SWP_NOACTIVATE);
                        }
                    }
                    else if(rc.top == barTop && rc.bottom - rc.top > height) {
                        PInvoke.SetWindowPos(container, IntPtr.Zero, rc.left, barTop + height, rc.right - rc.left,
                                rc.bottom - rc.top - height, SWP_NOZORDER | SWP_NOACTIVATE);
                    }
                }
                appliedHeight = height;

                if(!fParented) {
                    IntPtr hwndBar = bar.Handle;
                    int style = (int)PInvoke.GetWindowLongPtr(hwndBar, GWL.STYLE).ToInt64();
                    style = (style & ~WS_POPUP) | WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN;
                    PInvoke.SetWindowLongPtr(hwndBar, (int)GWL.STYLE, (IntPtr)style);
                    PInvoke.SetParent(hwndBar, hwndFrame);
                    fParented = true;
                }
                bool ok = PInvoke.SetWindowPos(bar.Handle, HWND_TOP, active.left, barTop, active.right - active.left, height,
                        SWP_NOACTIVATE | SWP_SHOWWINDOW);
                State("bar placed ok=" + ok + " at " + active.left + "," + barTop + " " + (active.right - active.left) + "x" + height);
            }
            catch(Exception e) {
                QTUtility2.MakeErrorLog(e, "Win11BarHost.Layout");
                Stop();
            }
        }
    }
}
