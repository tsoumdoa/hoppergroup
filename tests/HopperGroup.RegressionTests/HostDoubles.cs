using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.GUI.Canvas;
using HopperGroup;

// Minimal host doubles. Group rectangles follow member rectangles, as in Grasshopper.
// These tests exercise the unchanged manager source, not a replacement algorithm.
#if !REAL_WINFORMS
namespace System.Windows.Forms
{
    public enum MouseButtons { Left, Right }
    [Flags]
    public enum Keys { None = 0, Escape = 27, G = 71, H = 72, F6 = 117, Shift = 65536, Control = 131072, Alt = 262144 }
    public class KeyEventArgs : EventArgs
    {
        public KeyEventArgs() { }
        public KeyEventArgs(Keys key) { KeyCode = key; }
        public Keys KeyCode { get; set; }
        public Keys Modifiers { get; set; }
        public bool Handled { get; set; }
        public bool SuppressKeyPress { get; set; }
    }
    public class MouseEventArgs : EventArgs
    {
        public MouseButtons Button
        {
            get;
            set;
        } = MouseButtons.Left;
        public Point Location
        {
            get;
            set;
        }
    }
}
#endif
namespace Grasshopper
{
    public static class Instances
    {
        public static GH_Canvas ActiveCanvas;
        public static void InvalidateCanvas() { }
    }
}
namespace Grasshopper.GUI.Canvas
{
    public class GH_CanvasDocumentChangedEventArgs : EventArgs
    {
        public GH_Document OldDocument, NewDocument;
    }
#if !REAL_WINFORMS
    public class GH_Canvas
    {
        public static System.Windows.Forms.Keys NavigationPanLeft, NavigationPanRight,
            NavigationPanUp, NavigationPanDown, NavigationZoomIn, NavigationZoomOut;
        public bool ModifiersEnabled { get; set; } = true;
        public bool IsActiveInteraction, IsActiveWidget, IsActiveObject;
        public GH_Document Document;
        public bool Focused { get; set; } = true;
        public event EventHandler LostFocus;
        public event EventHandler<GH_CanvasDocumentChangedEventArgs> DocumentChanged;
        public event EventHandler<System.Windows.Forms.MouseEventArgs> MouseWheel;
        public event EventHandler<System.Windows.Forms.MouseEventArgs> MouseDown, MouseUp;
        public event EventHandler<System.Windows.Forms.KeyEventArgs> KeyDown;
        public event EventHandler<System.Windows.Forms.KeyEventArgs> KeyUp;
        public int HandlerCount => (MouseDown?.GetInvocationList().Length ?? 0) + (MouseUp?.GetInvocationList().Length ?? 0)
            + (KeyDown?.GetInvocationList().Length ?? 0)
            + (KeyUp?.GetInvocationList().Length ?? 0) + (LostFocus?.GetInvocationList().Length ?? 0)
            + (DocumentChanged?.GetInvocationList().Length ?? 0) + (MouseWheel?.GetInvocationList().Length ?? 0);
        public void ChangeDocument(GH_Document document)
        {
            var previous = Document;
            Document = document;
            DocumentChanged?.Invoke(this, new GH_CanvasDocumentChangedEventArgs { OldDocument = previous, NewDocument = document });
        }
        public void Wheel() => MouseWheel?.Invoke(this, new System.Windows.Forms.MouseEventArgs());
        public void Down() => MouseDown?.Invoke(this, new System.Windows.Forms.MouseEventArgs { Location = new Point(0, 0) });
        public void Up() => MouseUp?.Invoke(this, new System.Windows.Forms.MouseEventArgs { Location = new Point(100, 0) });
        public void ClickUp() => MouseUp?.Invoke(this, new System.Windows.Forms.MouseEventArgs { Location = new Point(0, 0) });
        public System.Windows.Forms.KeyEventArgs Press(System.Windows.Forms.Keys key, System.Windows.Forms.Keys modifiers = System.Windows.Forms.Keys.None)
        {
            var args = new System.Windows.Forms.KeyEventArgs { KeyCode = key, Modifiers = modifiers };
            KeyDown?.Invoke(this, args);
            return args;
        }
        public void Release(System.Windows.Forms.Keys key) => KeyUp?.Invoke(this, new System.Windows.Forms.KeyEventArgs { KeyCode = key });
        public void TapG() { Press(System.Windows.Forms.Keys.G); Release(System.Windows.Forms.Keys.G); }
        public void GG() { TapG(); TapG(); }
        public void Blur() { Focused = false; LostFocus?.Invoke(this, EventArgs.Empty); }
        public void Escape() => KeyDown?.Invoke(this, new System.Windows.Forms.KeyEventArgs { KeyCode = System.Windows.Forms.Keys.Escape });
    }
#endif
}
namespace Grasshopper.Kernel
{
    public class Attributes
    {
        private RectangleF bounds;
        public Func<RectangleF> Compute;
        public PointF Pivot { get; set; }
        public RectangleF Bounds
        {
            get => Compute == null ? bounds : Compute();
            set => bounds = value;
        }
    }
    public interface IGH_DocumentObject
    {
        Guid InstanceGuid
        {
            get;
        }
        Attributes Attributes
        {
            get;
        }
        string Name
        {
            get;
        }
        string NickName
        {
            get;
        }
    }
    public class Obj : IGH_DocumentObject
    {
        public Guid InstanceGuid
        {
            get;
        } = Guid.NewGuid();
        public Attributes Attributes
        {
            get;
        } = new Attributes();
        public string Name => "object";
        public string NickName => Name;
        public Obj(float x = 0, float y = 0)
        {
            Attributes.Pivot = new PointF(x, y);
            Attributes.Bounds = new RectangleF(x, y, 10, 10);
        }
        public void Move(float dx)
        {
            Attributes.Pivot = new PointF(Attributes.Pivot.X + dx, Attributes.Pivot.Y);
            var r = Attributes.Bounds;
            r.Offset(dx, 0);
            Attributes.Bounds = r;
        }
    }
    public class GH_DocObjectEventArgs : EventArgs
    {
        public List<IGH_DocumentObject> Objects = new();
    }
    public enum GH_UndoOperation { ClearUndoStack, ClearRedoStack, RecordAdded, RecordRemoved, Undo, Redo }
    public enum GH_ObjectEventType { Enabled }
    public class GH_ObjectChangedEventArgs : EventArgs
    {
        public GH_ObjectEventType Type;
    }
    public class GH_DocUndoEventArgs : EventArgs
    {
        public GH_UndoOperation Operation;
    }
    public class UndoServer
    {
        public List<string> UndoNames = new();
        public int UndoCount => UndoNames.Count;
    }
    public class UndoUtil
    {
        private UndoServer s;
        public UndoUtil(UndoServer server)
        {
            s = server;
        }
        public void RecordGenericObjectEvent(string name, GH_Group group) => s.UndoNames.Insert(0, name);
        public void MergeRecords(int n)
        {
            if (n > 1 && n <= s.UndoCount) s.UndoNames.RemoveRange(0, n - 1);
        }
    }
    public class GH_Document
    {
        public List<IGH_DocumentObject> Objects = new(), Selection = new();
        public UndoServer UndoServer = new();
        public UndoUtil UndoUtil;
        public GH_Document()
        {
            UndoUtil = new(UndoServer);
        }
        public bool IsModified;
        public event EventHandler<GH_DocObjectEventArgs> ObjectsAdded, ObjectsDeleted;
        public event EventHandler<GH_DocUndoEventArgs> UndoStateChanged;
        public int HandlerCount => (ObjectsAdded?.GetInvocationList().Length ?? 0)
            + (ObjectsDeleted?.GetInvocationList().Length ?? 0) + (UndoStateChanged?.GetInvocationList().Length ?? 0);
        // Tests restore the recorded layout first, matching the host's completed Undo/Redo event.
        public void RaiseUndoStateChanged(GH_UndoOperation operation) =>
            UndoStateChanged?.Invoke(this, new GH_DocUndoEventArgs { Operation = operation });
        public List<IGH_DocumentObject> SelectedObjects() => Selection;
        public void Add(IGH_DocumentObject obj)
        {
            Objects.Add(obj);
            ObjectsAdded?.Invoke(this, new GH_DocObjectEventArgs { Objects = new() { obj } });
        }
        public void Delete(IGH_DocumentObject obj)
        {
            Objects.Remove(obj);
            ObjectsDeleted?.Invoke(this, new GH_DocObjectEventArgs { Objects = new() { obj } });
        }
    }
}
namespace Grasshopper.Kernel.Special
{
    public class GH_Scribble : Obj
    {
        public GH_Scribble(float x = 0, float y = 0) : base(x, y) { }
    }

    public class GH_Group : Obj
    {
        public List<Guid> ObjectIDs = new();
        public GH_Group(GH_Document d)
        {
            Attributes.Compute = () =>
            {
                var members = d.Objects.Where(o => ObjectIDs.Contains(o.InstanceGuid)
                    && o.Attributes.Bounds.Width > 0 && o.Attributes.Bounds.Height > 0).ToList();
                if (members.Count == 0) return RectangleF.Empty;
                var b = members[0].Attributes.Bounds;
                foreach (var m in members.Skip(1)) b = RectangleF.Union(b, m.Attributes.Bounds);
                b.Inflate(10, 10);
                return b;
            };
        }
        public void CreateAttributes() { }
        public int CacheExpirations;
        public void ExpireCaches() { CacheExpirations++; }
        public void AddObject(Guid id)
        {
            if (!ObjectIDs.Contains(id)) ObjectIDs.Add(id);
        }
        public void RemoveObject(Guid id) => ObjectIDs.Remove(id);
    }
}
namespace HopperGroup
{
    // Native routing is covered separately with real WinForms HWNDs. This
    // membership suite keeps exercising the portable managed callback path.
#if !REAL_WINFORMS
    internal static class CanvasShortcutHook
    {
        internal static bool HasReservedShortcut(Grasshopper.GUI.Canvas.GH_Canvas canvas,
            System.Windows.Forms.Keys key) => false;
        internal static IDisposable Attach(Grasshopper.GUI.Canvas.GH_Canvas canvas,
            Func<System.Windows.Forms.Keys, bool> eligible,
            Action<System.Windows.Forms.Keys, bool> dispatch, Action reset) => null;
    }
#endif
    public class HopperGroupComponent : Obj
    {
        private bool locked;
        public event Action<IGH_DocumentObject, GH_ObjectChangedEventArgs> ObjectChanged;
        public bool Locked
        {
            get => locked;
            set
            {
                if (locked == value) return;
                locked = value;
                ObjectChanged?.Invoke(this, new GH_ObjectChangedEventArgs { Type = GH_ObjectEventType.Enabled });
            }
        }
        public int RefreshCount;
        public Func<string> ShortcutPromptProvider;
        public string Message { get; private set; } = string.Empty;
        public void UpdateShortcutPrompt() { Message = ShortcutPromptProvider?.Invoke() ?? string.Empty; }
        public void ScheduleOutputRefresh() { UpdateShortcutPrompt(); RefreshCount++; }
    }
}
namespace Rhino { public static class RhinoApp { public static void WriteLine(string s) { } } }
