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
namespace System.Windows.Forms
{
    public enum MouseButtons { Left, Right }
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
    public class GH_Canvas
    {
        public GH_Document Document;
        public event EventHandler<System.Windows.Forms.MouseEventArgs> MouseDown, MouseUp;
        public void Down() => MouseDown?.Invoke(this, new System.Windows.Forms.MouseEventArgs { Location = new Point(0, 0) });
        public void Up() => MouseUp?.Invoke(this, new System.Windows.Forms.MouseEventArgs { Location = new Point(100, 0) });
    }
}
namespace Grasshopper.Kernel
{
    public class Attributes
    {
        private RectangleF bounds;
        public Func<RectangleF> Compute;
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
            Attributes.Bounds = new RectangleF(x, y, 10, 10);
        }
        public void Move(float dx)
        {
            var r = Attributes.Bounds;
            r.Offset(dx, 0);
            Attributes.Bounds = r;
        }
    }
    public class GH_DocObjectEventArgs : EventArgs
    {
        public List<IGH_DocumentObject> Objects = new();
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
    public class GH_Group : Obj
    {
        public List<Guid> ObjectIDs = new();
        public GH_Document Document;
        public GH_Group(GH_Document d)
        {
            Document = d;
            Attributes.Compute = () =>
            {
                var members = d.Objects.Where(o => ObjectIDs.Contains(o.InstanceGuid)).ToList();
                if (members.Count == 0) return RectangleF.Empty;
                var b = members[0].Attributes.Bounds;
                foreach (var m in members.Skip(1)) b = RectangleF.Union(b, m.Attributes.Bounds);
                b.Inflate(10, 10);
                return b;
            };
        }
        public void CreateAttributes() { }
        public void ExpireCaches() { }
        public void AddObject(Guid id)
        {
            if (!ObjectIDs.Contains(id)) ObjectIDs.Add(id);
        }
        public void RemoveObject(Guid id) => ObjectIDs.Remove(id);
    }
}
namespace HopperGroup { public class HopperGroupComponent { public void ScheduleOutputRefresh() { } } }
namespace Rhino { public static class RhinoApp { public static void WriteLine(string s) { } } }
