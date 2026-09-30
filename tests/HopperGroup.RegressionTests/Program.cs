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
    static GH_Group AddGroup(GH_Document document, params IGH_DocumentObject[] members)
    {
        var group = new GH_Group(document);
        foreach (var member in members)
        {
            if (!document.Objects.Contains(member)) document.Add(member);
            group.AddObject(member.InstanceGuid);
        }
        document.Add(group);
        return group;
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
        Check("Dragging the only selected component preserves its group", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var child = AddGroup(d, o);
            d.Selection.Add(o);
            c.Down(); o.Move(500); c.Up();
            Assert(child.ObjectIDs.SequenceEqual(new[] { o.InstanceGuid }) && d.Objects.Contains(child)
                && m.GroupCount == 2 && m.LastChangeCount == 0,
                "dragging the only component emptied its group");
            m.RefreshAllObjects();
            Assert(child.ObjectIDs.Contains(o.InstanceGuid) && m.LastChangeCount == 0,
                "Refresh undid the single-component drag");
        });
        Check("The last remaining selected component carries its group", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            d.Selection.Add(a);
            c.Down(); a.Move(500); c.Up();
            Assert(g.ObjectIDs.SequenceEqual(new[] { b.InstanceGuid }), "setup did not leave one member");
            d.Selection.Clear(); d.Selection.Add(b);
            c.Down(); b.Move(1500); c.Up();
            Assert(g.ObjectIDs.SequenceEqual(new[] { b.InstanceGuid }) && m.LastChangeCount == 0,
                "the last remaining component emptied its group");
        });
        Check("Dragging the only component nests its group without flattening it", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var child = AddGroup(d, o);
            d.Selection.Add(o);
            c.Down();
            d.UndoServer.UndoNames.Insert(0, "Drag");
            o.Move(-970); c.Up();
            Assert(g.ObjectIDs.Contains(child.InstanceGuid) && !g.ObjectIDs.Contains(o.InstanceGuid)
                && child.ObjectIDs.SequenceEqual(new[] { o.InstanceGuid }),
                "transfer replaced the single-component group with its component");
            Assert(d.UndoServer.UndoCount == 1 && d.UndoServer.UndoNames[0] == "Drag",
                "single-component transfer did not merge with the native drag undo");
            m.RefreshAllObjects();
            Assert(g.ObjectIDs.Contains(child.InstanceGuid) && child.ObjectIDs.Contains(o.InstanceGuid)
                && m.LastChangeCount == 0, "Refresh undid the nested transfer");
        });
        Check("A single-component group needs full enclosure before nesting", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var child = AddGroup(d, o);
            d.Selection.Add(o);
            c.Down(); o.Move(-925); c.Up();
            Assert(!g.ObjectIDs.Contains(child.InstanceGuid) && !g.ObjectIDs.Contains(o.InstanceGuid)
                && child.ObjectIDs.Contains(o.InstanceGuid), "partial overlap flattened or nested the group");
            c.Down(); o.Move(-25); c.Up();
            Assert(g.ObjectIDs.Contains(child.InstanceGuid) && !g.ObjectIDs.Contains(o.InstanceGuid)
                && child.ObjectIDs.Contains(o.InstanceGuid), "fully enclosed group failed to nest");
        });
        Check("Dragging the only component out releases its group from its parent", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var child = AddGroup(d, a);
            g.RemoveObject(a.InstanceGuid); g.AddObject(child.InstanceGuid);
            d.Selection.Add(a);
            c.Down(); a.Move(500); c.Up();
            Assert(child.ObjectIDs.Contains(a.InstanceGuid) && !g.ObjectIDs.Contains(child.InstanceGuid)
                && g.ObjectIDs.SequenceEqual(new[] { b.InstanceGuid }),
                "exit emptied the child or left its old parent link");
            m.RefreshAllObjects();
            Assert(child.ObjectIDs.Contains(a.InstanceGuid) && !g.ObjectIDs.Contains(child.InstanceGuid)
                && m.LastChangeCount == 0, "Refresh undid the single-component exit");
        });
        Check("A single-component child honours its parent's exit buffer", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var child = AddGroup(d, a);
            g.RemoveObject(a.InstanceGuid); g.AddObject(child.InstanceGuid);
            d.Selection.Add(a);
            c.Down(); a.Move(120); c.Up();
            Assert(g.ObjectIDs.Contains(child.InstanceGuid) && child.ObjectIDs.Contains(a.InstanceGuid),
                "child left before clearing the buffered parent boundary");
            c.Down(); a.Move(150); c.Up();
            Assert(!g.ObjectIDs.Contains(child.InstanceGuid) && child.ObjectIDs.Contains(a.InstanceGuid),
                "child failed to leave after clearing the buffer");
        });
        Check("A single selected leaf carries its nested subtree into and out of a group", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var leaf = AddGroup(d, o);
            var root = AddGroup(d, leaf);
            var target = AddGroup(d, new Obj(400, -50), new Obj(600, 50));
            d.Selection.Add(o);
            c.Down(); o.Move(-500); c.Up();
            Assert(target.ObjectIDs.Contains(root.InstanceGuid) && root.ObjectIDs.Contains(leaf.InstanceGuid)
                && leaf.ObjectIDs.Contains(o.InstanceGuid) && !target.ObjectIDs.Contains(leaf.InstanceGuid)
                && !target.ObjectIDs.Contains(o.InstanceGuid), "single-component transfer flattened the subtree");
            c.Down(); o.Move(1000); c.Up();
            Assert(!target.ObjectIDs.Contains(root.InstanceGuid) && root.ObjectIDs.Contains(leaf.InstanceGuid)
                && leaf.ObjectIDs.Contains(o.InstanceGuid), "single-component exit damaged the subtree");
        });
        Check("A selected lone component transfers its group between siblings", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var member = new Obj(100);
            var child = AddGroup(d, member);
            var source = AddGroup(d, new Obj(0), new Obj(150), child);
            var target = AddGroup(d, new Obj(200), new Obj(350));
            var outer = AddGroup(d, source, target);
            d.Selection.Add(member); c.Down(); member.Move(110); c.Up();
            Assert(target.ObjectIDs.Contains(child.InstanceGuid) && !source.ObjectIDs.Contains(child.InstanceGuid)
                && outer.ObjectIDs.Contains(source.InstanceGuid) && outer.ObjectIDs.Contains(target.InstanceGuid)
                && child.ObjectIDs.Contains(member.InstanceGuid) && !target.ObjectIDs.Contains(member.InstanceGuid),
                "single-component sibling transfer lost the group or its direct parent link");
        });
        Check("A carried lone component survives reconciliation of an unrelated external move", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var child = AddGroup(d, o);
            a.Move(500);
            d.Selection.Add(o);
            c.Down(); o.Move(500); c.Up();
            Assert(!g.ObjectIDs.Contains(a.InstanceGuid) && child.ObjectIDs.Contains(o.InstanceGuid),
                "reconciling external movement emptied the carried single-component group");
        });
        Check("Single-component parent transfers survive Undo and Redo", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var child = AddGroup(d, o);
            d.Selection.Add(o);
            c.Down(); o.Move(-970); c.Up();
            Assert(g.ObjectIDs.Contains(child.InstanceGuid) && child.ObjectIDs.Contains(o.InstanceGuid),
                "setup transfer failed");
            o.Move(970); g.RemoveObject(child.InstanceGuid);
            d.RaiseUndoStateChanged(GH_UndoOperation.Undo);
            m.RefreshAllObjects();
            Assert(!g.ObjectIDs.Contains(child.InstanceGuid) && child.ObjectIDs.Contains(o.InstanceGuid)
                && m.LastChangeCount == 0, "Refresh undid the restored single-component layout");
            o.Move(-970); g.AddObject(child.InstanceGuid);
            d.RaiseUndoStateChanged(GH_UndoOperation.Redo);
            m.RefreshAllObjects();
            Assert(g.ObjectIDs.Contains(child.InstanceGuid) && child.ObjectIDs.Contains(o.InstanceGuid)
                && m.LastChangeCount == 0, "Refresh undid the restored single-component transfer");
            c.Down(); o.Move(970); c.Up();
            Assert(!g.ObjectIDs.Contains(child.InstanceGuid) && child.ObjectIDs.Contains(o.InstanceGuid),
                "dragging after Redo lost the component's group or retained its parent");
        });
        Check("Single-component parent exits respect zero Exit Scale", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var child = AddGroup(d, a);
            g.RemoveObject(a.InstanceGuid); g.AddObject(child.InstanceGuid);
            m.Configure(null, d, true, 0, false);
            d.Selection.Add(a);
            c.Down(); a.Move(100); c.Up();
            Assert(!g.ObjectIDs.Contains(child.InstanceGuid) && child.ObjectIDs.Contains(a.InstanceGuid),
                "zero Exit Scale retained the parent or emptied the child's group");
        });
        Check("An unselected lone member does not implicitly carry its group", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var child = AddGroup(d, o);
            c.Down(); o.Move(500); c.Up();
            Assert(!child.ObjectIDs.Contains(o.InstanceGuid),
                "an unselected member was mistaken for a selected majority");
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
        Check("Small member near the group edge is retained", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            d.Selection.Add(a);
            c.Down();
            a.Move(110);
            c.Up();
            Assert(g.ObjectIDs.Contains(a.InstanceGuid), "nearby member left its group too soon");
        });
        Check("Exit Scale still controls the removal distance", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            m.Configure(null, d, true, 0, false);
            d.Selection.Add(a);
            c.Down();
            a.Move(110);
            c.Up();
            Assert(!g.ObjectIDs.Contains(a.InstanceGuid), "zero exit scale kept the member");
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
        Check("Dragging all selected components carries an unselected group", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            d.Selection.Add(a);
            d.Selection.Add(b);
            c.Down();
            a.Move(500);
            b.Move(500);
            c.Up();
            Assert(g.ObjectIDs.Count == 2 && m.LastChangeCount == 0,
                "component drag removed members from their moving group");
        });
        Check("A coherent selected majority carries its group", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var third = new Obj(120);
            d.Add(third);
            g.AddObject(third.InstanceGuid);
            m.RefreshAllObjects();
            d.Selection.Add(a);
            d.Selection.Add(b);
            c.Down();
            a.Move(500);
            b.Move(500);
            c.Up();
            Assert(g.ObjectIDs.Contains(a.InstanceGuid) && g.ObjectIDs.Contains(b.InstanceGuid),
                "selected majority was ungrouped");
        });
        Check("Dragging selected components carries nested groups", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var child = new GH_Group(d);
            child.AddObject(a.InstanceGuid);
            child.AddObject(b.InstanceGuid);
            d.Add(child);
            g.RemoveObject(a.InstanceGuid);
            g.RemoveObject(b.InstanceGuid);
            g.AddObject(child.InstanceGuid);
            m.RefreshAllObjects();
            d.Selection.Add(a);
            d.Selection.Add(b);
            c.Down();
            a.Move(500);
            b.Move(500);
            c.Up();
            Assert(child.ObjectIDs.Contains(a.InstanceGuid) && child.ObjectIDs.Contains(b.InstanceGuid)
                && g.ObjectIDs.Contains(child.InstanceGuid), "nested group lost its moving components");
        });
        Check("One selected member does not carry a two-member group", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            d.Selection.Add(a);
            c.Down();
            a.Move(500);
            c.Up();
            Assert(!g.ObjectIDs.Contains(a.InstanceGuid), "half the group was treated as a majority");
        });
        Check("Selected members moving in different directions do not carry a group", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            d.Selection.Add(a);
            d.Selection.Add(b);
            c.Down();
            a.Move(500);
            b.Move(800);
            c.Up();
            Assert(g.ObjectIDs.Count == 0, "incoherent component motion carried the group");
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
            a.Move(140);
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
            // Leave two members after Redo so the later drag tests its boundary,
            // rather than carrying the group with its only selected component.
            var third = new Obj(120);
            d.Add(third); g.AddObject(third.InstanceGuid);
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
            c.Down(); b.Move(-60); c.Up();
            Assert(!g.ObjectIDs.Contains(b.InstanceGuid) && g.ObjectIDs.Contains(third.InstanceGuid),
                "member was retained by the group's pre-Redo boundary");
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
        Check("Manual addition at an external group's destination survives Refresh", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            a.Move(500); b.Move(500);
            var added = new Obj(530);
            d.Add(added);
            g.AddObject(added.InstanceGuid);
            m.RefreshAllObjects();
            Assert(g.ObjectIDs.Contains(a.InstanceGuid) && g.ObjectIDs.Contains(b.InstanceGuid)
                && g.ObjectIDs.Contains(added.InstanceGuid) && g.ObjectIDs.Count == 3
                && m.LastChangeCount == 0, "Refresh removed a member after an addition at the destination");
        });
        Check("Existing object grouped after an external move preserves old members", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            a.Move(1000); b.Move(1000);
            g.AddObject(o.InstanceGuid);
            m.RefreshAllObjects();
            Assert(g.ObjectIDs.Contains(a.InstanceGuid) && g.ObjectIDs.Contains(b.InstanceGuid)
                && g.ObjectIDs.Contains(o.InstanceGuid) && g.ObjectIDs.Count == 3
                && m.LastChangeCount == 0, "Refresh removed a member after grouping an existing object");
        });
        Check("Manual addition at an external group's destination survives a drag", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            a.Move(500); b.Move(500);
            var added = new Obj(530);
            d.Add(added);
            g.AddObject(added.InstanceGuid);
            c.Down(); a.Move(5); c.Up();
            Assert(g.ObjectIDs.Contains(a.InstanceGuid) && g.ObjectIDs.Contains(b.InstanceGuid)
                && g.ObjectIDs.Contains(added.InstanceGuid) && g.ObjectIDs.Count == 3
                && m.LastChangeCount == 0, "drag removed a member after an addition at the destination");
        });
        Check("New object moved before manual grouping keeps its current position", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var added = new Obj(10);
            d.Add(added);
            added.Move(500);
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
            manager.RefreshAllObjects();
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
            var added = new Obj(10);
            d.Add(added);
            added.Move(500);
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
        Check("Changing a child member does not remove the child group", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var innerMember = new Obj(30);
            d.Add(innerMember);
            var child = new GH_Group(d);
            child.AddObject(a.InstanceGuid);
            child.AddObject(innerMember.InstanceGuid);
            d.Add(child);
            g.RemoveObject(a.InstanceGuid);
            g.AddObject(child.InstanceGuid);
            m.RefreshAllObjects();
            d.Selection.Add(a);
            c.Down(); a.Move(500); c.Up();
            Assert(g.ObjectIDs.Contains(child.InstanceGuid)
                && child.ObjectIDs.Contains(innerMember.InstanceGuid)
                && !child.ObjectIDs.Contains(a.InstanceGuid), "member exit removed the nested group");
        });
        Check("A dragged group must fit inside the destination bounds", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var child = new GH_Group(d);
            child.AddObject(o.InstanceGuid);
            d.Add(child);
            d.Selection.Add(child);
            c.Down(); o.Move(-925); c.Up();
            Assert(!g.ObjectIDs.Contains(child.InstanceGuid), "partial overlap nested the group");
            c.Down(); o.Move(-25); c.Up();
            Assert(g.ObjectIDs.Contains(child.InstanceGuid), "fully enclosed group was not added");
        });
        Check("A dragged child keeps its members when leaving its parent", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var child = new GH_Group(d);
            child.AddObject(a.InstanceGuid);
            d.Add(child);
            g.RemoveObject(a.InstanceGuid);
            g.AddObject(child.InstanceGuid);
            m.RefreshAllObjects();
            d.Selection.Add(child);
            c.Down(); a.Move(500); c.Up();
            Assert(child.ObjectIDs.Contains(a.InstanceGuid) && !g.ObjectIDs.Contains(child.InstanceGuid),
                "child drag damaged its own membership or kept the parent link");
        });
        Check("F6 adds the captured selection after one group click", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            d.Selection.Add(o);
            c.F6();
            d.Selection.Clear(); d.Selection.Add(g);
            c.Down(); c.ClickUp();
            Assert(g.ObjectIDs.Contains(o.InstanceGuid) && m.LastChangeCount == 1,
                "F6 did not add the captured object");
        });
        Check("F6 does not add a group into its own descendant", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var child = new GH_Group(d);
            child.AddObject(o.InstanceGuid);
            d.Add(child);
            g.AddObject(child.InstanceGuid);
            d.Selection.Add(g);
            c.F6();
            d.Selection.Clear(); d.Selection.Add(child);
            c.Down(); c.ClickUp();
            Assert(!child.ObjectIDs.Contains(g.InstanceGuid) && m.LastChangeCount == 0,
                "F6 created a cyclic group hierarchy");
        });
        Check("F6 preserves unrelated pending external moves", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var x = new Obj(2000); var y = new Obj(2060);
            d.Add(x); d.Add(y);
            var target = new GH_Group(d); target.AddObject(x.InstanceGuid); target.AddObject(y.InstanceGuid); d.Add(target);
            a.Move(500);
            d.Selection.Add(o); c.F6();
            d.Selection.Clear(); d.Selection.Add(target); c.Down(); c.ClickUp();
            m.RefreshAllObjects();
            Assert(!g.ObjectIDs.Contains(a.InstanceGuid), "F6 erased unrelated pending external move; escaped member retained");
        });
        Check("Child joins inner destination while staying inside outer parent", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            b.Move(940);
            var left = new Obj(400); var right = new Obj(600); var member = new Obj(100);
            d.Add(left); d.Add(right); d.Add(member);
            var target = new GH_Group(d); target.AddObject(left.InstanceGuid); target.AddObject(right.InstanceGuid); d.Add(target);
            var child = new GH_Group(d); child.AddObject(member.InstanceGuid); d.Add(child);
            g.AddObject(child.InstanceGuid); g.AddObject(target.InstanceGuid);
            d.Selection.Add(child); c.Down(); member.Move(400); c.Up();
            Assert(target.ObjectIDs.Contains(child.InstanceGuid) && !g.ObjectIDs.Contains(child.InstanceGuid)
                && g.ObjectIDs.Contains(target.InstanceGuid) && child.ObjectIDs.Contains(member.InstanceGuid),
                "fully enclosed child failed to transfer to inner destination");
            Assert(d.UndoServer.UndoCount == 1, "nested transfer was split across undo records");
        });
        Check("F6 preserves pending moves through an unrelated native drag", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var target = AddGroup(d, new Obj(2000), new Obj(2060));
            a.Move(500);
            d.Selection.Add(o); c.F6();
            d.Selection.Clear(); d.Selection.Add(target); c.Down(); c.ClickUp();
            d.Selection.Clear(); d.Selection.Add(o); c.Down(); o.Move(5); c.Up();
            Assert(!g.ObjectIDs.Contains(a.InstanceGuid) && target.ObjectIDs.Contains(o.InstanceGuid),
                "F6 lost pending movement or the subsequent drag undid the addition");
        });
        Check("F6 into a nested child preserves pending member exits", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var sibling = new Obj(30);
            var child = AddGroup(d, a, sibling);
            g.RemoveObject(a.InstanceGuid); g.AddObject(child.InstanceGuid);
            m.RefreshAllObjects();
            var added = new Obj(20); d.Add(added);
            a.Move(500);
            d.Selection.Add(added); c.F6();
            d.Selection.Clear(); d.Selection.Add(child); c.Down(); c.ClickUp();
            m.RefreshAllObjects();
            Assert(!child.ObjectIDs.Contains(a.InstanceGuid) && child.ObjectIDs.Contains(added.InstanceGuid)
                && child.ObjectIDs.Contains(sibling.InstanceGuid) && g.ObjectIDs.Contains(child.InstanceGuid),
                "nested F6 edit erased an exit or damaged parent-child membership");
        });
        Check("F6 adds a subtree and later dragging it out preserves internal links", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var leaf = AddGroup(d, o);
            var root = AddGroup(d, leaf);
            d.Selection.Add(root); c.F6();
            d.Selection.Clear(); d.Selection.Add(g); c.Down(); c.ClickUp();
            Assert(g.ObjectIDs.Contains(root.InstanceGuid) && root.ObjectIDs.Contains(leaf.InstanceGuid)
                && leaf.ObjectIDs.Contains(o.InstanceGuid), "F6 flattened the subtree");
            m.RefreshAllObjects();
            d.Selection.Clear(); d.Selection.Add(root); c.Down(); o.Move(500); c.Up();
            Assert(!g.ObjectIDs.Contains(root.InstanceGuid) && root.ObjectIDs.Contains(leaf.InstanceGuid)
                && leaf.ObjectIDs.Contains(o.InstanceGuid), "subtree exit damaged internal links");
        });
        Check("F6 rejects a deep cycle while still adding valid selected objects", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var leaf = AddGroup(d, o);
            var middle = AddGroup(d, leaf);
            g.AddObject(middle.InstanceGuid);
            var added = new Obj(1030); d.Add(added);
            d.Selection.Add(g); d.Selection.Add(added); c.F6();
            d.Selection.Clear(); d.Selection.Add(leaf); c.Down(); c.ClickUp();
            Assert(!leaf.ObjectIDs.Contains(g.InstanceGuid) && leaf.ObjectIDs.Contains(added.InstanceGuid)
                && middle.ObjectIDs.Contains(leaf.InstanceGuid) && g.ObjectIDs.Contains(middle.InstanceGuid)
                && m.LastChangeCount == 1, "deep cycle was accepted or valid addition was lost");
        });
        Check("Escape cancels F6 without changing nested memberships or undo history", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var child = AddGroup(d, o);
            d.Selection.Add(child); c.F6(); c.Escape();
            d.Selection.Clear(); d.Selection.Add(g); c.Down(); c.ClickUp();
            Assert(!g.ObjectIDs.Contains(child.InstanceGuid) && child.ObjectIDs.Contains(o.InstanceGuid)
                && d.UndoServer.UndoCount == 0, "cancelled shortcut changed the document");
        });
        Check("Child transfers between siblings despite the old parent's exit buffer", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var member = new Obj(100);
            var child = AddGroup(d, member);
            var source = AddGroup(d, new Obj(0), new Obj(150), child);
            var target = AddGroup(d, new Obj(200), new Obj(350));
            var outer = AddGroup(d, source, target);
            d.Selection.Add(child); c.Down(); member.Move(110); c.Up();
            Assert(target.ObjectIDs.Contains(child.InstanceGuid) && !source.ObjectIDs.Contains(child.InstanceGuid)
                && outer.ObjectIDs.Contains(source.InstanceGuid) && outer.ObjectIDs.Contains(target.InstanceGuid)
                && child.ObjectIDs.Contains(member.InstanceGuid), "buffer blocked sibling transfer");
        });
        Check("Nested subtree enters a destination without flattening descendants", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var leaf = AddGroup(d, o);
            var root = AddGroup(d, leaf);
            var target = AddGroup(d, new Obj(400, -50), new Obj(600, 50));
            var outer = AddGroup(d, target, root, new Obj(0, -100));
            d.Selection.Add(root); c.Down(); o.Move(-500); c.Up();
            Assert(target.ObjectIDs.Contains(root.InstanceGuid) && !outer.ObjectIDs.Contains(root.InstanceGuid)
                && outer.ObjectIDs.Contains(target.InstanceGuid) && root.ObjectIDs.Contains(leaf.InstanceGuid)
                && !target.ObjectIDs.Contains(leaf.InstanceGuid) && leaf.ObjectIDs.Contains(o.InstanceGuid),
                "subtree transfer flattened descendants or retained the old direct parent");
        });
        Check("Externally moved child transfers to the inner destination on Refresh", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var child = AddGroup(d, o);
            var target = AddGroup(d, new Obj(400), new Obj(600));
            var outer = AddGroup(d, child, target, new Obj(0));
            m.RefreshAllObjects();
            o.Move(-500); m.RefreshAllObjects();
            Assert(target.ObjectIDs.Contains(child.InstanceGuid) && !outer.ObjectIDs.Contains(child.InstanceGuid)
                && outer.ObjectIDs.Contains(target.InstanceGuid) && child.ObjectIDs.Contains(o.InstanceGuid),
                "external transfer failed to preserve the subtree");
            m.RefreshAllObjects();
            Assert(m.LastChangeCount == 0, "nested transfer was not stable after Refresh");
        });
        Check("Child keeps its inner parent until its box clears the exit buffer", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var member = new Obj(100);
            var child = AddGroup(d, member);
            var parent = AddGroup(d, new Obj(0), new Obj(150), child);
            var outer = AddGroup(d, parent, new Obj(500, 50));
            d.Selection.Add(child); c.Down(); member.Move(105); c.Up();
            Assert(parent.ObjectIDs.Contains(child.InstanceGuid) && !outer.ObjectIDs.Contains(child.InstanceGuid),
                "partially overlapping buffered child was removed early");
            c.Down(); member.Move(150); c.Up();
            Assert(!parent.ObjectIDs.Contains(child.InstanceGuid) && outer.ObjectIDs.Contains(child.InstanceGuid)
                && child.ObjectIDs.Contains(member.InstanceGuid), "child did not transfer to outer after clearing buffer");
        });
        Check("Carried subtree drops a stationary second parent without losing its moving parent", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var member = new Obj(100);
            var child = AddGroup(d, member);
            var root = AddGroup(d, child);
            var stationary = AddGroup(d, child, new Obj(150));
            d.Selection.Add(root); c.Down(); member.Move(500); c.Up();
            Assert(root.ObjectIDs.Contains(child.InstanceGuid) && !stationary.ObjectIDs.Contains(child.InstanceGuid)
                && child.ObjectIDs.Contains(member.InstanceGuid), "stationary parent followed an unrelated carried subtree");
        });
        Check("External nested exit survives an unrelated native drag", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var child = AddGroup(d, a);
            g.RemoveObject(a.InstanceGuid); g.AddObject(child.InstanceGuid);
            m.RefreshAllObjects();
            a.Move(500);
            d.Selection.Add(o); c.Down(); o.Move(100); c.Up();
            Assert(child.ObjectIDs.Contains(a.InstanceGuid) && !g.ObjectIDs.Contains(child.InstanceGuid),
                "unrelated drag retained escaped child's parent link");
            m.RefreshAllObjects();
            Assert(!g.ObjectIDs.Contains(child.InstanceGuid) && m.LastChangeCount == 0,
                "Refresh changed the reconciled nested exit");
        });
        Check("External subtree entry survives an unrelated native drag", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var leaf = AddGroup(d, o);
            var child = AddGroup(d, leaf);
            var target = AddGroup(d, new Obj(400, -50), new Obj(600, 50));
            var outer = AddGroup(d, child, target, new Obj(0, -100));
            m.RefreshAllObjects();
            o.Move(-500);
            d.Selection.Add(a); c.Down();
            d.UndoServer.UndoNames.Insert(0, "Drag");
            var undoCount = d.UndoServer.UndoCount;
            a.Move(-500); c.Up();
            Assert(target.ObjectIDs.Contains(child.InstanceGuid) && !outer.ObjectIDs.Contains(child.InstanceGuid)
                && child.ObjectIDs.Contains(leaf.InstanceGuid) && !target.ObjectIDs.Contains(leaf.InstanceGuid)
                && leaf.ObjectIDs.Contains(o.InstanceGuid),
                "unrelated drag lost the pending transfer or flattened the subtree");
            Assert(d.UndoServer.UndoCount == undoCount, "pending transfer was split from the native drag undo record");
            m.RefreshAllObjects();
            Assert(target.ObjectIDs.Contains(child.InstanceGuid) && m.LastChangeCount == 0,
                "Refresh changed the reconciled nested transfer");
        });
        Console.WriteLine("Failed: " + failures);
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }
}
