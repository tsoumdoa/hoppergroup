using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using HopperGroup;

internal static class Program
{
    private const int KeyDown = 0x100, KeyUp = 0x101, Character = 0x102;
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint key, uint mode);
    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
    private static int _failures;

    [STAThread]
    private static int Main()
    {
        Application.EnableVisualStyles();
        using var form = new Form { KeyPreview = true, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Location = new Point(-10000, -10000), Size = new Size(500, 300) };
        form.Shown += (_, _) => form.BeginInvoke((Action)(() =>
        {
            RunTests(form);
            form.Close();
        }));
        Application.Run(form);
        Console.WriteLine($"Failed: {_failures}");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(Form form, string name, Action<Fixture> test)
    {
        try
        {
            using var fixture = new Fixture(form);
            test(fixture);
            Console.WriteLine("PASS " + name);
        }
        catch (Exception exception)
        {
            _failures++;
            Console.WriteLine("FAIL " + name + ": " + exception.Message);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static IntPtr KeyData(Keys key, bool up = false, bool repeat = false)
    {
        var value = 1L | ((long)MapVirtualKey((uint)key, 0) << 16);
        if (repeat || up) value |= 1L << 30;
        if (up) value |= 1L << 31;
        return new IntPtr(value);
    }

    private static void QueueTap(Control control, Keys key)
    {
        Assert(PostMessage(control.Handle, KeyDown, new IntPtr((int)key), KeyData(key)), "PostMessage down failed");
        Assert(PostMessage(control.Handle, KeyUp, new IntPtr((int)key), KeyData(key, up: true)), "PostMessage up failed");
    }

    private static void Tap(Control control, Keys key)
    {
        QueueTap(control, key);
        Application.DoEvents();
    }

    private static void RunTests(Form form)
    {
        Check(form, "Baseline KeyPreview consumes G before canvas KeyDown", f =>
        {
            f.Manager.Configure(null, f.Document, false, 1, false);
            Tap(f.Canvas, Keys.G);
            Assert(f.ForwardedKeys == 1 && f.Characters == 1, "baseline did not exercise editor/TranslateMessage routing");
        });
        Check(form, "Real queued GG consumes both editor keys and translated characters", f =>
        {
            QueueTap(f.Canvas, Keys.G); QueueTap(f.Canvas, Keys.G); Application.DoEvents();
            Assert(f.Manager.ShortcutPrompt.Length > 0 && f.ForwardedKeys == 0 && f.Characters == 0,
                "GG failed or escaped to editor");
        });
        Check(form, "Queued G-H-G resets taps without leaking delayed G characters", f =>
        {
            QueueTap(f.Canvas, Keys.G); QueueTap(f.Canvas, Keys.H); QueueTap(f.Canvas, Keys.G); Application.DoEvents();
            Assert(f.Manager.ShortcutPrompt.Length == 0 && f.ForwardedKeys == 1 && f.Characters == 1,
                "foreign key completed GG or captured G characters leaked");
            Tap(f.Canvas, Keys.G);
            Assert(f.Manager.ShortcutPrompt.Length > 0, "fresh next tap failed");
        });
        Check(form, "Auto-repeat after a focus interruption never supplies a second tap", f =>
        {
            Tap(f.Canvas, Keys.G);
            using var editor = new TextBox(); form.Controls.Add(editor); editor.Focus(); f.Canvas.Focus();
            Assert(PostMessage(f.Canvas.Handle, KeyDown, new IntPtr((int)Keys.G), KeyData(Keys.G, repeat: true)), "repeat post failed");
            Assert(PostMessage(f.Canvas.Handle, KeyUp, new IntPtr((int)Keys.G), KeyData(Keys.G, up: true)), "keyup post failed");
            Application.DoEvents(); Tap(f.Canvas, Keys.G);
            Assert(f.Manager.ShortcutPrompt.Length == 0, "a held repeat became a tap");
            Tap(f.Canvas, Keys.G);
            Assert(f.Manager.ShortcutPrompt.Length > 0, "fresh GG did not recover");
        });
        Check(form, "Text input focus preserves G typing", f =>
        {
            using var editor = new TextBox(); form.Controls.Add(editor); editor.Focus();
            Tap(editor, Keys.G);
            Assert(editor.Text == "g" && f.ForwardedKeys == 0 && f.Manager.ShortcutPrompt.Length == 0,
                "canvas hook stole editor input");
        });
        Check(form, "Ctrl-G remains available to the editor", f =>
        {
            var original = new byte[256]; Assert(GetKeyboardState(original), "GetKeyboardState failed");
            var modified = (byte[])original.Clone(); modified[(int)Keys.ControlKey] = 0x80;
            try
            {
                Assert(SetKeyboardState(modified), "SetKeyboardState failed");
                SendMessage(f.Canvas.Handle, KeyDown, new IntPtr((int)Keys.G), KeyData(Keys.G));
                SendMessage(f.Canvas.Handle, KeyUp, new IntPtr((int)Keys.G), KeyData(Keys.G, up: true));
                Assert(f.ForwardedKeys == 1 && f.Manager.ShortcutPrompt.Length == 0, "modified key was intercepted");
            }
            finally { SetKeyboardState(original); }
        });
        Check(form, "Empty selection and active interactions preserve host G", f =>
        {
            f.Document.Selection.Clear(); Tap(f.Canvas, Keys.G);
            f.Document.Selection.Add(f.Object);
            f.Canvas.ModifiersEnabled = false; Tap(f.Canvas, Keys.G); f.Canvas.ModifiersEnabled = true;
            f.Canvas.IsActiveInteraction = true; Tap(f.Canvas, Keys.G); f.Canvas.IsActiveInteraction = false;
            f.Canvas.IsActiveWidget = true; Tap(f.Canvas, Keys.G); f.Canvas.IsActiveWidget = false;
            f.Canvas.IsActiveObject = true; Tap(f.Canvas, Keys.G); f.Canvas.IsActiveObject = false;
            Assert(f.ForwardedKeys == 5 && f.Manager.ShortcutPrompt.Length == 0, "host interaction G was intercepted");
        });
        Check(form, "Switching documents away and back invalidates the first tap", f =>
        {
            Tap(f.Canvas, Keys.G); f.Canvas.ChangeDocument(new GH_Document()); f.Canvas.ChangeDocument(f.Document);
            Tap(f.Canvas, Keys.G);
            Assert(f.Manager.ShortcutPrompt.Length == 0, "a tap survived document changes");
            Tap(f.Canvas, Keys.G); Assert(f.Manager.ShortcutPrompt.Length > 0, "fresh GG failed");
        });
        Check(form, "Mouse wheel invalidates the first tap", f =>
        {
            Tap(f.Canvas, Keys.G); f.Canvas.Wheel(); Tap(f.Canvas, Keys.G);
            Assert(f.Manager.ShortcutPrompt.Length == 0, "mouse wheel retained first tap");
        });
        Check(form, "Handle recreation resets taps and reattaches hook", f =>
        {
            Tap(f.Canvas, Keys.G); f.Canvas.Recreate(); f.Canvas.Focus(); Tap(f.Canvas, Keys.G);
            Assert(f.Manager.ShortcutPrompt.Length == 0 && f.ForwardedKeys == 0, "recreated HWND was not safely hooked");
            Tap(f.Canvas, Keys.G); Assert(f.Manager.ShortcutPrompt.Length > 0, "recreated hook did not activate");
        });
        Check(form, "Multiple enabled components have a single owner and transfer after disable", f =>
        {
            using var second = new GroupMembershipManager(() => 0);
            second.Configure(null, f.Document, true, 1, false);
            Tap(f.Canvas, Keys.G); Tap(f.Canvas, Keys.G);
            Assert(f.Manager.ShortcutPrompt.Length > 0 && second.ShortcutPrompt.Length == 0, "duplicate ownership");
            f.Manager.Configure(null, f.Document, false, 1, false);
            Tap(f.Canvas, Keys.G); Assert(second.ShortcutPrompt.Length == 0, "disable transferred stale first tap");
            Tap(f.Canvas, Keys.G); Assert(second.ShortcutPrompt.Length > 0 && f.ForwardedKeys == 0, "next owner failed");
        });
        Check(form, "Navigation reservations preserve plain G and menu scanner respects modified bindings", f =>
        {
            GH_Canvas.NavigationPanLeft = Keys.G;
            try { Tap(f.Canvas, Keys.G); }
            finally { GH_Canvas.NavigationPanLeft = Keys.None; }
            using var menu = new MenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Reserved") { ShortcutKeys = Keys.Control | Keys.G }); form.Controls.Add(menu);
            Assert(CanvasShortcutHook.HasReservedShortcut(f.Canvas, Keys.Control | Keys.G)
                && !CanvasShortcutHook.HasReservedShortcut(f.Canvas, Keys.G), "reservation scanner missed exact binding");
            Assert(f.ForwardedKeys == 1 && f.Manager.ShortcutPrompt.Length == 0, "reserved navigation key was stolen");
        });
        Check(form, "Canvas Escape cancels GG and consumes its translated character", f =>
        {
            Tap(f.Canvas, Keys.G); Tap(f.Canvas, Keys.G); Tap(f.Canvas, Keys.Escape);
            Assert(f.Manager.ShortcutPrompt.Length == 0 && f.ForwardedKeys == 0 && f.Characters == 0,
                "Escape failed to cancel or escaped to the editor");
        });
        foreach (var groupLast in new[] { false, true })
        Check(form, "Escape cancels both Group and a peer canvas subscriber (Group last: " + groupLast + ")", f =>
        {
            if (groupLast) f.Manager.Configure(null, f.Document, false, 1, false);
            var cancelled = false;
            KeyEventHandler peerEscape = (_, e) =>
            {
                if (e.KeyCode != Keys.Escape) return;
                cancelled = true; e.Handled = true; e.SuppressKeyPress = true;
            };
            f.Canvas.KeyDown += peerEscape;
            try
            {
                if (groupLast) f.Manager.Configure(null, f.Document, true, 1, false);
                Tap(f.Canvas, Keys.G); Tap(f.Canvas, Keys.G); Tap(f.Canvas, Keys.Escape);
                Assert(cancelled && f.Manager.ShortcutPrompt.Length == 0 && f.Characters == 0,
                    "one shortcut owner consumed Escape before the other could cancel");
            }
            finally { f.Canvas.KeyDown -= peerEscape; }
        });
        Check(form, "Preprocessed native menu commands interrupt GG through their key release", f =>
        {
            var clicks = 0;
            using var menu = new MenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Command", null, (_, _) => clicks++) { ShortcutKeys = Keys.F5 });
            form.Controls.Add(menu); form.MainMenuStrip = menu; f.Canvas.Focus();
            QueueTap(f.Canvas, Keys.G); QueueTap(f.Canvas, Keys.F5); QueueTap(f.Canvas, Keys.G); Application.DoEvents();
            Assert(clicks == 1 && f.Manager.ShortcutPrompt.Length == 0, "native menu preprocessing hid the interruption");
            form.MainMenuStrip = null;
        });
        Check(form, "Preprocessed native menu press interrupts GG before its key release", f =>
        {
            var clicks = 0;
            using var menu = new MenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Command", null, (_, _) => clicks++) { ShortcutKeys = Keys.F5 });
            form.Controls.Add(menu); form.MainMenuStrip = menu; f.Canvas.Focus();
            QueueTap(f.Canvas, Keys.G);
            Assert(PostMessage(f.Canvas.Handle, KeyDown, new IntPtr((int)Keys.F5), KeyData(Keys.F5)), "F5 down post failed");
            QueueTap(f.Canvas, Keys.G); Application.DoEvents();
            Assert(clicks == 1 && f.Manager.ShortcutPrompt.Length == 0, "preprocessed held F5 did not reset the first G tap");
            Assert(PostMessage(f.Canvas.Handle, KeyUp, new IntPtr((int)Keys.F5), KeyData(Keys.F5, up: true)), "F5 up post failed");
            Application.DoEvents(); form.MainMenuStrip = null;
        });
        foreach (var groupOuter in new[] { false, true })
        Check(form, "Peer plugin preserves mixed-key interruptions (Group outer: " + groupOuter + ")", f =>
        {
            if (groupOuter) f.Manager.Configure(null, f.Document, false, 1, false);
            using var peer = new PeerDrawingHook(f.Canvas);
            if (groupOuter) f.Manager.Configure(null, f.Document, true, 1, false);
            QueueTap(f.Canvas, Keys.G); QueueTap(f.Canvas, Keys.D); QueueTap(f.Canvas, Keys.G); Application.DoEvents();
            Assert(f.Manager.ShortcutPrompt.Length == 0, "G-D-G activated GG through peer hook");
            QueueTap(f.Canvas, Keys.D); QueueTap(f.Canvas, Keys.G); QueueTap(f.Canvas, Keys.D); Application.DoEvents();
            Assert(peer.Activations == 0, "D-G-D activated peer through Group hook");
            Tap(f.Canvas, Keys.G); Tap(f.Canvas, Keys.G);
            Assert(f.Manager.ShortcutPrompt.Length > 0, "GG stopped working with peer hook installed");
            Tap(f.Canvas, Keys.D); Tap(f.Canvas, Keys.D);
            Assert(peer.Activations == 1 && f.ForwardedKeys == 0 && f.Characters == 0,
                "peer DD stopped working or captured keys/characters escaped");
            // Detaching an inner NativeWindow must not break the outer subclass.
            if (groupOuter)
            {
                peer.Dispose(); f.Manager.Configure(null, f.Document, false, 1, false);
                f.Manager.Configure(null, f.Document, true, 1, false);
                Tap(f.Canvas, Keys.G); Tap(f.Canvas, Keys.G);
                Assert(f.Manager.ShortcutPrompt.Length > 0, "inner peer disposal broke Group hook");
            }
            else
            {
                f.Manager.Configure(null, f.Document, false, 1, false);
                Tap(f.Canvas, Keys.D); Tap(f.Canvas, Keys.D);
                Assert(peer.Activations == 2, "inner Group disposal broke peer hook");
            }
        });
        Check(form, "Layout-dependent G characters are consumed by physical scan code", f =>
        {
            SendMessage(f.Canvas.Handle, KeyDown, new IntPtr((int)Keys.G), KeyData(Keys.G));
            SendMessage(f.Canvas.Handle, KeyUp, new IntPtr((int)Keys.G), KeyData(Keys.G, up: true));
            SendMessage(f.Canvas.Handle, Character, new IntPtr('\u0443'), KeyData(Keys.G));
            Assert(f.ForwardedKeys == 0 && f.Characters == 0, "non-Latin mapped character escaped");
        });
        Check(form, "Disposal removes hook and restores original editor routing", f =>
        {
            f.Manager.Dispose(); Tap(f.Canvas, Keys.G);
            Assert(f.ForwardedKeys == 1 && f.Characters == 1, "disposed hook kept consuming G");
        });
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly GH_Document Document = new GH_Document();
        internal readonly Obj Object = new Obj();
        internal readonly GH_Canvas Canvas;
        internal readonly GroupMembershipManager Manager = new GroupMembershipManager(() => 0);
        internal int ForwardedKeys, Characters;
        private readonly Form _form;
        private readonly KeyEventHandler _keyHandler;
        private readonly KeyPressEventHandler _characterHandler;

        internal Fixture(Form form)
        {
            _form = form;
            Canvas = new GH_Canvas { Document = Document, Location = new Point(5, 30), Size = new Size(300, 200) };
            Document.Add(Object); Document.Selection.Add(Object); Instances.ActiveCanvas = Canvas;
            form.Controls.Add(Canvas); form.Activate(); Assert(Canvas.Focus(), "canvas focus failed");
            _keyHandler = (_, e) =>
            {
                if (Canvas.Focused && e.KeyCode >= Keys.A && e.KeyCode <= Keys.Z)
                { ForwardedKeys++; e.Handled = true; }
            };
            _characterHandler = (_, e) => { if (Canvas.Focused) { Characters++; e.Handled = true; } };
            form.KeyDown += _keyHandler; form.KeyPress += _characterHandler;
            Manager.Configure(null, Document, true, 1, false);
        }

        public void Dispose()
        {
            Manager.Dispose(); Canvas.Dispose(); Instances.ActiveCanvas = null;
            _form.KeyDown -= _keyHandler; _form.KeyPress -= _characterHandler;
        }
    }

    // Independent plugin stand-in using the shared observation protocol, with
    // its own NativeWindow in either subclass order. No Draw project dependency.
    private sealed class PeerDrawingHook : NativeWindow, IDisposable
    {
        private readonly int _observed = RegisterWindowMessage("Hopper.Grasshopper.CanvasShortcut.KeyObserved.v1");
        private int _taps, _characters;
        internal int Activations;
        internal PeerDrawingHook(Control canvas) => AssignHandle(canvas.Handle);
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == _observed)
            {
                if ((Keys)message.WParam.ToInt32() != Keys.D) _taps = 0;
                base.WndProc(ref message); return;
            }
            if (message.Msg == KeyDown)
            {
                SendMessage(Handle, _observed, message.WParam, new IntPtr(message.Msg));
                if ((Keys)message.WParam.ToInt32() == Keys.D)
                {
                    _characters++;
                    if (++_taps == 2) { _taps = 0; Activations++; }
                    return;
                }
            }
            else if (message.Msg == KeyUp && (Keys)message.WParam.ToInt32() == Keys.D) return;
            else if (message.Msg == Character && _characters > 0
                && ((message.LParam.ToInt64() >> 16) & 0xff) == MapVirtualKey((uint)Keys.D, 0))
            {
                _characters--; return;
            }
            base.WndProc(ref message);
        }
        public void Dispose() { if (Handle != IntPtr.Zero) ReleaseHandle(); }
    }
}
