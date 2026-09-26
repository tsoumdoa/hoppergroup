using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.GUI.Canvas;
using HopperGroup;

class Program
{
    static int failures;
    static void Check(string name, Action action)
    {
        try
        {
            action();
            Console.WriteLine("PASS " + name);
        }
        catch (Exception e)
        {
            failures++;
            Console.WriteLine("FAIL " + name + ": " + e.Message);
        }
    }
    static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    static (GH_Document, GH_Canvas, GroupMembershipManager, GH_Group, Obj, Obj, Obj) Setup()
    {
        var d = new GH_Document();
        Obj a = new Obj(0), b = new Obj(60), other = new Obj(1000);
        d.Objects.AddRange(new IGH_DocumentObject[] { a, b, other });
        var g = new GH_Group(d);
        d.Objects.Add(g);
        g.AddObject(a.InstanceGuid);
        g.AddObject(b.InstanceGuid);
        var c = new GH_Canvas { Document = d };
        Grasshopper.Instances.ActiveCanvas = c;
        var m = new GroupMembershipManager();
        m.Configure(null, d, true, 1, false);
        return (d, c, m, g, a, b, other);
    }
    static void Main()
    {
        Check("Click does not change membership", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            d.Selection.Add(a);
            c.Down();
            c.Up();
            Assert(g.ObjectIDs.Count == 2 && m.LastChangeCount == 0, "click mutated group");
        });
        Check("Single member dragged out is removed", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            d.Selection.Add(a);
            c.Down();
            a.Move(500);
            c.Up();
            Assert(!g.ObjectIDs.Contains(a.InstanceGuid), "moved member retained");
        });
        Check("Carried group retains members", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            d.Selection.Add(g);
            c.Down();
            a.Move(500);
            b.Move(500);
            c.Up();
            Assert(g.ObjectIDs.Count == 2, "carried group lost members");
        });
        Check("Unselected object dragged into group is added", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            c.Down();
            o.Move(-970);
            c.Up();
            Assert(g.ObjectIDs.Contains(o.InstanceGuid), "incoming member not added");
        });
        Check("Keyboard/script move out then Refresh removes member", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            a.Move(500);
            m.RefreshAllObjects();
            Assert(!g.ObjectIDs.Contains(a.InstanceGuid), "Refresh retained moved-out member");
        });
        Check("External move survives unrelated drag before Refresh", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            a.Move(500);
            c.Down();
            o.Move(100);
            c.Up();
            m.RefreshAllObjects();
            Assert(!g.ObjectIDs.Contains(a.InstanceGuid), "unrelated drag erased pending external movement");
        });
        Check("Whole group moved externally then Refresh preserves members", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            a.Move(500);
            b.Move(500);
            m.RefreshAllObjects();
            Assert(g.ObjectIDs.Count == 2, "Refresh emptied translated group: " + g.ObjectIDs.Count + " members");
        });
        Check("Native drag and memberships form one undo record", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            d.Selection.Add(a);
            c.Down();
            d.UndoServer.UndoNames.Insert(0, "Drag");
            a.Move(500);
            c.Up();
            Assert(d.UndoServer.UndoCount == 1, "undo records: " + d.UndoServer.UndoCount);
        });
        Check("Click preserves pending external movement for Refresh", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            a.Move(500);
            c.Down();
            c.Up();
            Assert(g.ObjectIDs.Contains(a.InstanceGuid), "click reconciled pending move");
            m.RefreshAllObjects();
            Assert(!g.ObjectIDs.Contains(a.InstanceGuid), "click lost pending move");
        });
        Check("External whole-group move survives an unrelated drag", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            a.Move(500);
            b.Move(500);
            c.Down();
            o.Move(100);
            c.Up();
            m.RefreshAllObjects();
            Assert(g.ObjectIDs.Count == 2, "intervening drag destroyed translated group");
        });
        Check("Members with different external displacements leave separately", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            a.Move(500);
            b.Move(800);
            m.RefreshAllObjects();
            Assert(g.ObjectIDs.Count == 0, "incoherent motion treated as a carried group");
        });
        Check("External move followed by dragging the same member uses old boundary", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            a.Move(500);
            c.Down();
            a.Move(100);
            c.Up();
            Assert(!g.ObjectIDs.Contains(a.InstanceGuid), "drag reset original boundary");
        });
        Check("Refresh is idempotent after external movement", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            a.Move(500);
            m.RefreshAllObjects();
            var count = d.UndoServer.UndoCount;
            m.RefreshAllObjects();
            Assert(m.LastChangeCount == 0 && d.UndoServer.UndoCount == count, "second refresh mutated document");
        });
        Check("Translated group does not capture stationary objects in its old footprint", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            o.Move(-970);
            m.Configure(null, d, false, 1, false);
            m.Configure(null, d, true, 1, false);
            a.Move(500);
            b.Move(500);
            m.RefreshAllObjects();
            Assert(g.ObjectIDs.Count == 2 && !g.ObjectIDs.Contains(o.InstanceGuid), "old footprint captured stationary object");
        });
        Check("External nested child escapes parent without losing members", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var child = new GH_Group(d);
            d.Add(child);
            child.AddObject(a.InstanceGuid);
            g.RemoveObject(a.InstanceGuid);
            g.AddObject(child.InstanceGuid);
            m.RefreshAllObjects();
            a.Move(500);
            m.RefreshAllObjects();
            Assert(child.ObjectIDs.Contains(a.InstanceGuid), "child lost member");
            Assert(!g.ObjectIDs.Contains(child.InstanceGuid), "parent stretched around moved child");
        });
        Check("Nested subtree translated externally stays intact", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var child = new GH_Group(d);
            d.Add(child);
            child.AddObject(a.InstanceGuid);
            g.RemoveObject(a.InstanceGuid);
            g.AddObject(child.InstanceGuid);
            m.RefreshAllObjects();
            a.Move(500);
            b.Move(500);
            m.RefreshAllObjects();
            Assert(child.ObjectIDs.Contains(a.InstanceGuid) && g.ObjectIDs.Contains(child.InstanceGuid)
             && g.ObjectIDs.Contains(b.InstanceGuid), "nested subtree changed membership");
        });
        Check("Canvas-carried nested child escapes parent", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var child = new GH_Group(d);
            d.Add(child);
            child.AddObject(a.InstanceGuid);
            g.RemoveObject(a.InstanceGuid);
            g.AddObject(child.InstanceGuid);
            m.RefreshAllObjects();
            d.Selection.Add(child);
            c.Down();
            a.Move(500);
            c.Up();
            Assert(child.ObjectIDs.Contains(a.InstanceGuid) && !g.ObjectIDs.Contains(child.InstanceGuid), "nested drag failed");
        });
        Check("Dragging members without selecting their group still releases them", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            o.Move(100);
            c.Down();
            a.Move(500);
            b.Move(500);
            c.Up();
            Assert(g.ObjectIDs.Count == 0, "unrelated external movement changed native drag semantics");
        });
        Check("Native carried group stays intact while another external move is reconciled", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            o.Move(100);
            d.Selection.Add(g);
            c.Down();
            a.Move(500);
            b.Move(500);
            c.Up();
            Assert(g.ObjectIDs.Count == 2, "external reconciliation lost native carried context");
        });
        Check("Placement without mouse-down preserves pending external history", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            a.Move(500);
            var added = new Obj(1200);
            d.Add(added);
            d.Selection.Add(added);
            c.Up();
            m.RefreshAllObjects();
            Assert(!g.ObjectIDs.Contains(a.InstanceGuid), "placement erased pending history");
        });
        Check("Two-group transfer merges membership records with only the current drag", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var target = new GH_Group(d);
            d.Add(target);
            target.AddObject(o.InstanceGuid);
            m.RefreshAllObjects();
            d.UndoServer.UndoNames.Clear();
            d.UndoServer.UndoNames.Add("Earlier edit");
            c.Down();
            d.UndoServer.UndoNames.Insert(0, "Drag");
            a.Move(1000);
            c.Up();
            Assert(!g.ObjectIDs.Contains(a.InstanceGuid) && target.ObjectIDs.Contains(a.InstanceGuid), "transfer failed");
            Assert(d.UndoServer.UndoCount == 2 && d.UndoServer.UndoNames[0] == "Drag"
             && d.UndoServer.UndoNames[1] == "Earlier edit", "unrelated undo history merged");
        });
        Check("Refresh merges all membership changes into one undo record", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var target = new GH_Group(d);
            d.Add(target);
            target.AddObject(o.InstanceGuid);
            m.RefreshAllObjects();
            d.UndoServer.UndoNames.Clear();
            a.Move(1000);
            m.RefreshAllObjects();
            Assert(!g.ObjectIDs.Contains(a.InstanceGuid) && target.ObjectIDs.Contains(a.InstanceGuid), "external transfer failed");
            Assert(d.UndoServer.UndoCount == 1, "refresh split into multiple undo records");
        });
        Check("Another document's mouse events do not change this document", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            c.Document = new GH_Document();
            c.Down();
            a.Move(500);
            c.Up();
            Assert(g.ObjectIDs.Contains(a.InstanceGuid), "wrong document processed");
        });
        Check("Small objects use their own exit buffer in mixed-size drags", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            o.Attributes.Bounds = new RectangleF(1000, 1000, 1000, 1000);
            c.Down();
            a.Move(110);
            o.Move(10);
            c.Up();
            Assert(!g.ObjectIDs.Contains(a.InstanceGuid), "large selection inflated small object's buffer");
        });
        Check("Translated group does not capture a moved object entering its old footprint", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            a.Move(500);
            b.Move(500);
            o.Move(-970);
            m.RefreshAllObjects();
            Assert(g.ObjectIDs.Count == 2 && !g.ObjectIDs.Contains(o.InstanceGuid), "old footprint captured moved object");
        });
        Check("Translated group accepts a moved object at its destination", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            a.Move(500);
            b.Move(500);
            o.Move(-470);
            m.RefreshAllObjects();
            Assert(g.ObjectIDs.Count == 3 && g.ObjectIDs.Contains(o.InstanceGuid), "destination did not receive moved object");
        });
        Check("Undo-restored membership survives Refresh", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            d.Selection.Add(a);
            c.Down();
            d.UndoServer.UndoNames.Insert(0, "Drag");
            a.Move(500);
            c.Up();
            Assert(!g.ObjectIDs.Contains(a.InstanceGuid), "setup drag failed");
            a.Move(-500);
            g.AddObject(a.InstanceGuid);
            d.UndoServer.UndoNames.Clear();
            d.RaiseUndoStateChanged(GH_UndoOperation.Undo);
            m.Configure(null, d, true, 1, false);
            m.RefreshAllObjects();
            Assert(g.ObjectIDs.Contains(a.InstanceGuid), "Refresh removed the restored member");
            Assert(m.LastChangeCount == 0 && d.UndoServer.UndoCount == 0, "Refresh recorded a new edit after Undo");
        });
        Check("Undo-restored membership survives an unrelated drag", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            d.Selection.Add(a);
            c.Down(); a.Move(500); c.Up();
            a.Move(-500);
            g.AddObject(a.InstanceGuid);
            d.RaiseUndoStateChanged(GH_UndoOperation.Undo);
            c.Down(); o.Move(10); c.Up();
            Assert(g.ObjectIDs.Contains(a.InstanceGuid), "unrelated drag removed the restored member");
        });
        Check("Redo resets the boundary before a later member drag", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            d.Selection.Add(a);
            c.Down(); a.Move(500); c.Up();
            Assert(!g.ObjectIDs.Contains(a.InstanceGuid), "setup drag failed");
            a.Move(-500);
            g.AddObject(a.InstanceGuid);
            d.RaiseUndoStateChanged(GH_UndoOperation.Undo);
            a.Move(500);
            g.RemoveObject(a.InstanceGuid);
            d.RaiseUndoStateChanged(GH_UndoOperation.Redo);
            d.Selection.Clear();
            d.Selection.Add(b);
            c.Down(); b.Move(-50); c.Up();
            Assert(g.ObjectIDs.Count == 0, "member was retained by the group's pre-Redo boundary");
        });
        Check("Undo stack notifications preserve pending external movement", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            a.Move(500);
            d.RaiseUndoStateChanged(GH_UndoOperation.RecordAdded);
            d.RaiseUndoStateChanged(GH_UndoOperation.ClearRedoStack);
            m.RefreshAllObjects();
            Assert(!g.ObjectIDs.Contains(a.InstanceGuid), "stack notification erased pending movement");
        });
        Check("External group translation followed by a small member drag keeps both members", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            a.Move(500); b.Move(500);
            d.Selection.Add(a);
            c.Down(); a.Move(5); c.Up();
            Assert(g.ObjectIDs.Count == 2, "small member drag emptied the translated group");
        });
        Check("External group translation followed by a large member drag releases only that member", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            a.Move(500); b.Move(500);
            d.Selection.Add(a);
            c.Down(); a.Move(500); c.Up();
            Assert(!g.ObjectIDs.Contains(a.InstanceGuid) && g.ObjectIDs.Contains(b.InstanceGuid),
                "native drag did not use the translated boundary");
        });
        Check("Dragging a translated member back to its settled position still releases it", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            a.Move(500); b.Move(500);
            d.Selection.Add(a);
            c.Down(); a.Move(-500); c.Up();
            Assert(!g.ObjectIDs.Contains(a.InstanceGuid) && g.ObjectIDs.Contains(b.InstanceGuid),
                "returning to an old position bypassed reconciliation");
        });
        Check("Native child drag escapes an externally translated parent", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var child = new GH_Group(d);
            d.Add(child);
            child.AddObject(a.InstanceGuid);
            g.RemoveObject(a.InstanceGuid);
            g.AddObject(child.InstanceGuid);
            m.RefreshAllObjects();
            a.Move(500); b.Move(500);
            d.Selection.Add(child);
            c.Down(); a.Move(500); c.Up();
            Assert(child.ObjectIDs.Contains(a.InstanceGuid) && g.ObjectIDs.Contains(b.InstanceGuid)
                && !g.ObjectIDs.Contains(child.InstanceGuid), "translated parent stretched around the native child drag");
        });
        Console.WriteLine("Failed: " + failures);
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }
}
