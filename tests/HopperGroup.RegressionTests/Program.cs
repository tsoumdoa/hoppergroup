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
        Check("Re-enabling uses the layout edited while disabled", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            m.Configure(null, d, false, 1, false);
            a.Move(500); b.Move(500);
            m.Configure(null, d, true, 1, false);
            a.Move(5);
            m.RefreshAllObjects();
            Assert(g.ObjectIDs.Count == 2 && m.LastChangeCount == 0, "re-enable used the old boundary");
            a.Move(500);
            m.RefreshAllObjects();
            Assert(!g.ObjectIDs.Contains(a.InstanceGuid) && g.ObjectIDs.Contains(b.InstanceGuid),
                "re-enable stopped later exits from being processed");
        });
        Check("Manual addition expands the boundary before an external member move", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            g.AddObject(o.InstanceGuid);
            a.Move(500);
            m.RefreshAllObjects();
            Assert(g.ObjectIDs.Count == 3 && d.UndoServer.UndoCount == 0, "manual addition kept a stale boundary");
        });
        Check("New manual member joins a coherent external group move", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var added = new Obj(30);
            d.Add(added);
            g.AddObject(added.InstanceGuid);
            a.Move(500); b.Move(500); added.Move(500);
            c.Down(); c.Up();
            m.RefreshAllObjects();
            Assert(g.ObjectIDs.Contains(a.InstanceGuid) && g.ObjectIDs.Contains(b.InstanceGuid)
                && g.ObjectIDs.Contains(added.InstanceGuid) && g.ObjectIDs.Count == 3,
                "coherent move lost a group member added since the last settled layout");
        });
        Check("New object moved before manual grouping keeps its current position", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var added = new Obj(1000);
            d.Add(added);
            added.Move(-500);
            g.AddObject(added.InstanceGuid);
            m.RefreshAllObjects();
            Assert(g.ObjectIDs.Contains(a.InstanceGuid) && g.ObjectIDs.Contains(b.InstanceGuid)
                && g.ObjectIDs.Contains(added.InstanceGuid) && g.ObjectIDs.Count == 3,
                "Refresh undid a manual addition made after the object moved");
        });
        Check("New nested member moves with its child and parent groups", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var child = new GH_Group(d);
            child.AddObject(a.InstanceGuid);
            d.Add(child);
            g.RemoveObject(a.InstanceGuid);
            g.AddObject(child.InstanceGuid);
            m.RefreshAllObjects();
            var added = new Obj(30);
            d.Add(added);
            child.AddObject(added.InstanceGuid);
            a.Move(500); b.Move(500); added.Move(500);
            m.RefreshAllObjects();
            Assert(child.ObjectIDs.Contains(a.InstanceGuid) && child.ObjectIDs.Contains(added.InstanceGuid)
                && child.ObjectIDs.Count == 2 && g.ObjectIDs.Contains(child.InstanceGuid)
                && g.ObjectIDs.Contains(b.InstanceGuid) && g.ObjectIDs.Count == 2,
                "coherent nested move lost a member or parent-child relationship");
        });
        Check("New nested member leaves parent with its child group", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var child = new GH_Group(d);
            child.AddObject(a.InstanceGuid);
            d.Add(child);
            g.RemoveObject(a.InstanceGuid);
            g.AddObject(child.InstanceGuid);
            m.RefreshAllObjects();
            var added = new Obj(30);
            d.Add(added);
            child.AddObject(added.InstanceGuid);
            a.Move(500); added.Move(500);
            m.RefreshAllObjects();
            Assert(child.ObjectIDs.Contains(a.InstanceGuid) && child.ObjectIDs.Contains(added.InstanceGuid)
                && child.ObjectIDs.Count == 2 && !g.ObjectIDs.Contains(child.InstanceGuid)
                && g.ObjectIDs.Contains(b.InstanceGuid),
                "moving the nested child away damaged its members or retained the old parent");
        });
        Check("New nested member stays with child dragged after an external parent move", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var child = new GH_Group(d);
            child.AddObject(a.InstanceGuid);
            d.Add(child);
            g.RemoveObject(a.InstanceGuid);
            g.AddObject(child.InstanceGuid);
            m.RefreshAllObjects();
            var added = new Obj(30);
            d.Add(added);
            child.AddObject(added.InstanceGuid);
            a.Move(500); b.Move(500); added.Move(500);
            d.Selection.Add(child);
            c.Down();
            a.Move(500); added.Move(500);
            c.Up();
            Assert(child.ObjectIDs.Contains(a.InstanceGuid) && child.ObjectIDs.Contains(added.InstanceGuid)
                && child.ObjectIDs.Count == 2 && !g.ObjectIDs.Contains(child.InstanceGuid)
                && g.ObjectIDs.Contains(b.InstanceGuid),
                "native child drag after external parent movement damaged nested membership");
        });
        Check("New member leaving an inner group does not absorb stationary ancestors", () =>
        {
            var d = new GH_Document();
            var innerMember = new Obj(0);
            var middleMember = new Obj(60);
            var outerMember = new Obj(120);
            var inner = new GH_Group(d);
            var middle = new GH_Group(d);
            var outer = new GH_Group(d);
            d.Objects.AddRange(new IGH_DocumentObject[]
                { innerMember, middleMember, outerMember, inner, middle, outer });
            inner.AddObject(innerMember.InstanceGuid);
            middle.AddObject(inner.InstanceGuid);
            middle.AddObject(middleMember.InstanceGuid);
            outer.AddObject(middle.InstanceGuid);
            outer.AddObject(outerMember.InstanceGuid);
            var canvas = new GH_Canvas { Document = d };
            Grasshopper.Instances.ActiveCanvas = canvas;
            var manager = new GroupMembershipManager();
            manager.Configure(null, d, true, 1, false);
            var added = new Obj(10);
            d.Add(added);
            inner.AddObject(added.InstanceGuid);
            added.Move(500);
            manager.RefreshAllObjects();
            Assert(!inner.ObjectIDs.Contains(added.InstanceGuid)
                && inner.ObjectIDs.Contains(innerMember.InstanceGuid)
                && middle.ObjectIDs.Contains(inner.InstanceGuid)
                && middle.ObjectIDs.Contains(middleMember.InstanceGuid)
                && outer.ObjectIDs.Contains(middle.InstanceGuid)
                && outer.ObjectIDs.Contains(outerMember.InstanceGuid),
                "moving one new inner member changed stationary nested memberships");
        });
        Check("New object moved before joining a nested child stays in that child", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var child = new GH_Group(d);
            child.AddObject(a.InstanceGuid);
            d.Add(child);
            g.RemoveObject(a.InstanceGuid);
            g.AddObject(child.InstanceGuid);
            m.RefreshAllObjects();
            var added = new Obj(1000);
            d.Add(added);
            added.Move(-500);
            child.AddObject(added.InstanceGuid);
            m.RefreshAllObjects();
            Assert(child.ObjectIDs.Contains(a.InstanceGuid) && child.ObjectIDs.Contains(added.InstanceGuid)
                && child.ObjectIDs.Contains(b.InstanceGuid) && g.ObjectIDs.Contains(child.InstanceGuid)
                && !g.ObjectIDs.Contains(b.InstanceGuid),
                "Refresh undid a nested manual addition or left a member outside its innermost group");
        });
        Check("Manual addition still allows an external member to leave the enlarged group", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            g.AddObject(o.InstanceGuid);
            a.Move(1500);
            m.RefreshAllObjects();
            Assert(!g.ObjectIDs.Contains(a.InstanceGuid) && g.ObjectIDs.Count == 2, "live outline swallowed the exit");
        });
        Check("Manual removal shrinks the boundary before an external member move", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            g.AddObject(o.InstanceGuid);
            m.RefreshAllObjects();
            g.RemoveObject(o.InstanceGuid);
            a.Move(500);
            m.RefreshAllObjects();
            Assert(g.ObjectIDs.Count == 1 && g.ObjectIDs.Contains(b.InstanceGuid), "removed member left an oversized boundary");
        });
        Check("Manual nested membership edits update ancestor boundaries", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var child = new GH_Group(d);
            child.AddObject(a.InstanceGuid);
            d.Add(child);
            g.RemoveObject(a.InstanceGuid);
            g.AddObject(child.InstanceGuid);
            m.RefreshAllObjects();
            child.AddObject(o.InstanceGuid);
            a.Move(500);
            m.RefreshAllObjects();
            Assert(child.ObjectIDs.Contains(a.InstanceGuid) && child.ObjectIDs.Contains(o.InstanceGuid)
                && g.ObjectIDs.Contains(child.InstanceGuid) && g.ObjectIDs.Contains(b.InstanceGuid),
                "nested membership edit used an old boundary");
        });
        Check("Manual group edits preserve unrelated pending external movement", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var target = new GH_Group(d);
            var far = new Obj(1500);
            d.Add(far);
            target.AddObject(o.InstanceGuid);
            d.Add(target);
            m.RefreshAllObjects();
            a.Move(500);
            target.AddObject(far.InstanceGuid);
            o.Move(100);
            m.RefreshAllObjects();
            Assert(!g.ObjectIDs.Contains(a.InstanceGuid) && target.ObjectIDs.Count == 2,
                "manual edit erased another group's pending movement");
        });
        Check("Disabled input detaches handlers and does no work on canvas or document events", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var owner = new HopperGroupComponent();
            m.Configure(owner, d, true, 1, true);
            m.Configure(owner, d, false, 1, true);
            var log = m.DebugLog;
            var cacheExpirations = g.CacheExpirations;
            c.Down(); a.Move(500); c.Up();
            var added = new Obj(30);
            d.Add(added); d.Selection.Add(added); c.Up();
            var otherGroup = new GH_Group(d);
            d.Add(otherGroup); d.Delete(otherGroup);
            d.RaiseUndoStateChanged(GH_UndoOperation.Undo);
            d.RaiseUndoStateChanged(GH_UndoOperation.Redo);
            m.RefreshAllObjects();
            Assert(c.HandlerCount == 0 && d.HandlerCount == 0, "disabled manager kept event handlers");
            Assert(g.ObjectIDs.Count == 2 && !g.ObjectIDs.Contains(added.InstanceGuid)
                && d.UndoServer.UndoCount == 0 && !d.IsModified, "disabled manager edited memberships or undo history");
            Assert(g.CacheExpirations == cacheExpirations && m.DebugLog == log && owner.RefreshCount == 0,
                "disabled manager scanned groups, logged, or recomputed its owner");
            Assert(m.LastChangeCount == 0 && m.Status == "Disabled", "disabled status retained old changes");
        });
        Check("Grasshopper Disable stops an in-progress drag and unlock uses the current layout", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var owner = new HopperGroupComponent();
            m.Configure(owner, d, true, 1, false);
            c.Down();
            owner.Locked = true;
            a.Move(500); b.Move(500); c.Up();
            m.RefreshAllObjects();
            Assert(g.ObjectIDs.Count == 2 && d.UndoServer.UndoCount == 0 && owner.RefreshCount == 0,
                "host-disabled component still processed movement");
            Assert(c.HandlerCount == 0 && d.HandlerCount == 0, "host Disable left event handlers attached");
            owner.Locked = false;
            a.Move(5);
            m.RefreshAllObjects();
            Assert(g.ObjectIDs.Count == 2, "unlock restored the stale boundary");
            c.Down(); a.Move(500); c.Up();
            Assert(!g.ObjectIDs.Contains(a.InstanceGuid), "unlock did not resume later drags");
        });
        Check("A component loaded locked stays inactive even with Enabled true", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var owner = new HopperGroupComponent { Locked = true };
            m.Configure(owner, d, true, 1, false);
            c.Down(); a.Move(500); c.Up();
            m.RefreshAllObjects();
            Assert(g.ObjectIDs.Count == 2 && d.UndoServer.UndoCount == 0
                && c.HandlerCount == 0 && d.HandlerCount == 0, "loaded disabled component ran");
        });
        Check("Unlocking does not override a false Enabled input", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var owner = new HopperGroupComponent();
            m.Configure(owner, d, false, 1, false);
            owner.Locked = true;
            owner.Locked = false;
            c.Down(); a.Move(500); c.Up();
            m.RefreshAllObjects();
            Assert(g.ObjectIDs.Count == 2 && d.UndoServer.UndoCount == 0
                && c.HandlerCount == 0 && d.HandlerCount == 0, "unlock overrode Enabled=false");
        });
        Check("Disposed manager cannot be restarted by an owner enable event", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var owner = new HopperGroupComponent();
            m.Configure(owner, d, true, 1, false);
            m.Dispose();
            owner.Locked = true; owner.Locked = false;
            c.Down(); a.Move(500); c.Up();
            m.RefreshAllObjects();
            Assert(g.ObjectIDs.Count == 2 && c.HandlerCount == 0 && d.HandlerCount == 0,
                "disposed manager resumed handling events");
        });
        Console.WriteLine("Failed: " + failures);
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }
}
