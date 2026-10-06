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
    static (GH_Document, GH_Canvas, GroupMembershipManager, GH_Group, Obj, Obj, Obj) Setup(bool scribbleMember = false, Func<long> shortcutTimestamp = null)
    {
        var d = new GH_Document();
        Obj a = new Obj(0), b = scribbleMember ? new GH_Scribble(60) : new Obj(60), other = new Obj(1000);
        d.Objects.AddRange(new IGH_DocumentObject[] { a, b, other });
        var g = new GH_Group(d);
        d.Objects.Add(g);
        g.AddObject(a.InstanceGuid);
        g.AddObject(b.InstanceGuid);
        var c = new GH_Canvas { Document = d };
        Grasshopper.Instances.ActiveCanvas = c;
        var m = new GroupMembershipManager(shortcutTimestamp);
        m.Configure(null, d, true, 1, false);
        return (d, c, m, g, a, b, other);
    }
    static void Main()
    {
        Check("Clicking a newly grouped lone component does not nest its group", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var added = new Obj(30);
            var child = AddGroup(d, added);
            d.Selection.Add(added);
            var undoCount = d.UndoServer.UndoCount;
            c.Down(); c.ClickUp();
            Assert(!g.ObjectIDs.Contains(child.InstanceGuid) && !g.ObjectIDs.Contains(added.InstanceGuid)
                && child.ObjectIDs.SequenceEqual(new[] { added.InstanceGuid }) && m.LastChangeCount == 0
                && d.UndoServer.UndoCount == undoCount && !d.IsModified,
                "a stationary newly grouped component changed membership or created an undo record");
            // The initial click must not prevent an actual later drag from carrying the group.
            c.Down(); added.Move(5); c.Up();
            Assert(g.ObjectIDs.Contains(child.InstanceGuid) && child.ObjectIDs.Contains(added.InstanceGuid)
                && !g.ObjectIDs.Contains(added.InstanceGuid), "a real drag after the click failed to carry the group");
        });
        Check("Clicking a newly grouped leaf does not nest its subtree", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var target = AddGroup(d, new Obj(400, -50), new Obj(600, 50));
            var added = new Obj(500);
            var leaf = AddGroup(d, added);
            var root = AddGroup(d, leaf);
            d.Selection.Add(added);
            c.Down(); c.ClickUp();
            Assert(!target.ObjectIDs.Contains(root.InstanceGuid) && !target.ObjectIDs.Contains(leaf.InstanceGuid)
                && root.ObjectIDs.SequenceEqual(new[] { leaf.InstanceGuid })
                && leaf.ObjectIDs.SequenceEqual(new[] { added.InstanceGuid }) && m.LastChangeCount == 0
                && d.UndoServer.UndoCount == 0, "a stationary new leaf nested its subtree or created an undo record");
        });
        foreach (var selectGroup in new[] { false, true })
        {
            Check($"Clicking newly grouped components preserves overlapping groups (group selected: {selectGroup})", () =>
            {
                var (d, c, m, g, a, b, o) = Setup();
                var target = AddGroup(d, new Obj(270), new Obj(330));
                var outer = AddGroup(d, g, target, new Obj(-100, -50), new Obj(450, 50));
                var left = new Obj(30);
                var right = new Obj(300);
                var manual = AddGroup(d, left, right);
                d.Selection.Add(left); d.Selection.Add(right);
                if (selectGroup) d.Selection.Add(manual);
                c.Down(); c.ClickUp();
                Assert(manual.ObjectIDs.SequenceEqual(new[] { left.InstanceGuid, right.InstanceGuid })
                    && !g.ObjectIDs.Contains(left.InstanceGuid) && !target.ObjectIDs.Contains(right.InstanceGuid)
                    && !outer.ObjectIDs.Contains(manual.InstanceGuid)
                    && m.LastChangeCount == 0 && d.UndoServer.UndoCount == 0 && !d.IsModified,
                    "a click transferred stationary members into smaller groups or nested their group into the outer group");
                d.Selection.Clear(); d.Selection.Add(left);
                c.Down(); left.Move(1000); c.Up();
                Assert(manual.ObjectIDs.SequenceEqual(new[] { right.InstanceGuid }),
                    "preserving the click prevented a later individual member exit");
            });
        }
        Check("New placement preserves stationary manual memberships in the same selection", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var left = new Obj(30);
            var right = new Obj(300);
            var manual = AddGroup(d, left, right);
            d.Selection.Add(left); d.Selection.Add(right);
            c.Down();
            // A placement can arrive after mouse-down, with no movement snapshot.
            var added = new Obj(40);
            d.Add(added); d.Selection.Add(added); c.Up();
            Assert(g.ObjectIDs.Contains(added.InstanceGuid) && !g.ObjectIDs.Contains(left.InstanceGuid)
                && manual.ObjectIDs.SequenceEqual(new[] { left.InstanceGuid, right.InstanceGuid })
                && m.LastChangeCount == 1 && d.UndoServer.UndoCount == 1,
                "placement was skipped or reprocessed a stationary manually grouped component");
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
            m.RefreshAllObjects();
            Assert(g.ObjectIDs.SequenceEqual(new[] { b.InstanceGuid }) && m.LastChangeCount == 0,
                "Refresh undid the single-component drag");
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
            Assert(g.ObjectIDs.SequenceEqual(new[] { b.InstanceGuid }) && child.ObjectIDs.Contains(a.InstanceGuid),
                "child failed to leave after clearing the buffer");
            m.RefreshAllObjects();
            Assert(child.ObjectIDs.Contains(a.InstanceGuid) && !g.ObjectIDs.Contains(child.InstanceGuid)
                && m.LastChangeCount == 0, "Refresh undid the single-component exit");
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
        Check("A selected group with an empty nested group keeps its moving members", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var empty = AddGroup(d);
            g.AddObject(empty.InstanceGuid);
            d.Selection.Add(g);
            c.Down(); a.Move(500); b.Move(500); c.Up();
            Assert(g.ObjectIDs.Contains(a.InstanceGuid) && g.ObjectIDs.Contains(b.InstanceGuid)
                && g.ObjectIDs.Contains(empty.InstanceGuid) && m.LastChangeCount == 0,
                "an empty descendant made the moving components fall out of their selected group");
        });
        Check("A selected group ignores members without usable canvas bounds when detecting a carry", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var invisible = new Obj { Attributes = { Bounds = RectangleF.Empty } };
            d.Add(invisible); g.AddObject(invisible.InstanceGuid);
            d.Selection.Add(g);
            c.Down(); a.Move(500); b.Move(500); invisible.Move(500); c.Up();
            Assert(g.ObjectIDs.Count == 3 && m.LastChangeCount == 0,
                "a member excluded from movement tracking invalidated the carried group");
        });
        Check("Component layout changes during a selected drag do not break group carrying", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            d.Selection.Add(a); d.Selection.Add(b);
            c.Down(); a.Move(500); b.Move(500);
            var bounds = a.Attributes.Bounds;
            bounds.Width += 30;
            a.Attributes.Bounds = bounds;
            c.Up();
            Assert(g.ObjectIDs.Count == 2 && m.LastChangeCount == 0,
                "a layout change was mistaken for independent component movement");
        });
        Check("Canvas rounding during a selected drag does not break group carrying", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            d.Selection.Add(a); d.Selection.Add(b);
            c.Down(); a.Move(500); b.Move(500.4f); c.Up();
            Assert(g.ObjectIDs.Count == 2 && m.LastChangeCount == 0,
                "sub-unit layout rounding caused moving components to fall out");
        });
        foreach (var nested in new[] { false, true })
        {
            foreach (var pendingExternalMove in new[] { false, true })
            {
                Check($"A group-title drag keeps members rounded to zero movement (nested: {nested}, external: {pendingExternalMove})", () =>
                {
                    var (d, c, m, g, a, b, o) = Setup(scribbleMember: true);
                    var overlapping = AddGroup(d, new Obj(60));
                    var selectedGroup = nested ? AddGroup(d, g) : g;
                    d.Selection.Add(selectedGroup);
                    if (pendingExternalMove) o.Move(500);
                    c.Down();
                    // A component layout rounds the shared delta to zero; a scribble
                    // can keep its fractional pivot after the same native group drag.
                    b.Move(0.4f);
                    c.Up();
                    Assert(g.ObjectIDs.SequenceEqual(new[] { a.InstanceGuid, b.InstanceGuid })
                        && !overlapping.ObjectIDs.Contains(b.InstanceGuid)
                        && (!nested || selectedGroup.ObjectIDs.SequenceEqual(new[] { g.InstanceGuid }))
                        && m.LastChangeCount == 0 && d.UndoServer.UndoCount == 0,
                        "a zero-rounded member invalidated group carrying and let an overlap steal another member");
                });
            }
        }
        Check("A layout-only change during a click does not detach a stationary component", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var overlapping = AddGroup(d, new Obj(25));
            d.Selection.Add(a);
            c.Down();
            var bounds = a.Attributes.Bounds;
            bounds.Width += 30;
            a.Attributes.Bounds = bounds;
            c.ClickUp();
            Assert(g.ObjectIDs.Count == 2 && !overlapping.ObjectIDs.Contains(a.InstanceGuid)
                && d.UndoServer.UndoCount == 0,
                "a stationary pivot was processed as a member drag");
            d.Selection.Clear(); d.Selection.Add(o);
            c.Down(); o.Move(10); c.Up();
            Assert(g.ObjectIDs.Count == 2 && !overlapping.ObjectIDs.Contains(a.InstanceGuid)
                && d.UndoServer.UndoCount == 0,
                "the layout change was replayed as external movement on the next drag");
        });
        Check("Rotating a scribble in place does not transfer its membership", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var scribble = new GH_Scribble(60);
            d.Add(scribble); g.AddObject(scribble.InstanceGuid);
            var overlapping = AddGroup(d, new Obj(60));
            d.Selection.Add(scribble);
            c.Down();
            // Native scribble Pivot is corner A. A half-turn changes that corner
            // while leaving the center and the axis-aligned bounds unchanged.
            scribble.Attributes.Pivot = new PointF(70, 10);
            c.Up();
            Assert(g.ObjectIDs.Contains(scribble.InstanceGuid)
                && !overlapping.ObjectIDs.Contains(scribble.InstanceGuid)
                && m.LastChangeCount == 0 && d.UndoServer.UndoCount == 0,
                "scribble rotation was mistaken for a drag into the overlapping group");
            d.Selection.Clear(); d.Selection.Add(o);
            c.Down(); o.Move(10); c.Up();
            Assert(g.ObjectIDs.Contains(scribble.InstanceGuid)
                && !overlapping.ObjectIDs.Contains(scribble.InstanceGuid)
                && m.LastChangeCount == 0 && d.UndoServer.UndoCount == 0,
                "scribble rotation was replayed as external movement on the next drag");
        });
        foreach (var external in new[] { false, true })
        {
            Check($"A translated scribble still leaves its group (external: {external})", () =>
            {
                var (d, c, m, g, a, b, o) = Setup();
                var scribble = new GH_Scribble(30);
                d.Add(scribble); g.AddObject(scribble.InstanceGuid);
                m.Configure(null, d, false, 1, false);
                m.Configure(null, d, true, 1, false);
                if (!external)
                {
                    d.Selection.Add(scribble);
                    c.Down();
                }
                scribble.Move(500);
                if (external) m.RefreshAllObjects(); else c.Up();
                Assert(!g.ObjectIDs.Contains(scribble.InstanceGuid)
                    && g.ObjectIDs.Contains(a.InstanceGuid) && g.ObjectIDs.Contains(b.InstanceGuid)
                    && m.LastChangeCount == 1,
                    "ignoring scribble rotation also ignored a real translation");
            });
        }
        Check("An empty nested group does not prevent an individual member from leaving", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var empty = AddGroup(d);
            g.AddObject(empty.InstanceGuid);
            d.Selection.Add(g);
            c.Down(); a.Move(500); c.Up();
            Assert(!g.ObjectIDs.Contains(a.InstanceGuid) && g.ObjectIDs.Contains(b.InstanceGuid)
                && g.ObjectIDs.Contains(empty.InstanceGuid) && m.LastChangeCount == 1,
                "ignoring an empty descendant protected a real individual exit");
        });
        foreach (var reconcileOnDrop in new[] { false, true })
        {
            Check($"External group carrying tolerates layout changes and rounding (drop: {reconcileOnDrop})", () =>
            {
                var (d, c, m, g, a, b, o) = Setup();
                a.Move(500); b.Move(500.4f);
                var bounds = a.Attributes.Bounds;
                bounds.Width += 30;
                a.Attributes.Bounds = bounds;
                if (reconcileOnDrop)
                {
                    d.Selection.Add(o);
                    c.Down(); o.Move(10); c.Up();
                }
                else
                {
                    m.RefreshAllObjects();
                }
                Assert(g.ObjectIDs.Count == 2 && m.LastChangeCount == 0,
                    "external reconciliation detached members with matching pivot movement");
            });
            Check($"External group carrying ignores empty descendants and unusable bounds (drop: {reconcileOnDrop})", () =>
            {
                var (d, c, m, g, a, b, o) = Setup();
                var invisible = new Obj { Attributes = { Bounds = RectangleF.Empty } };
                var child = AddGroup(d, invisible);
                g.AddObject(child.InstanceGuid);
                m.Configure(null, d, false, 1, false);
                m.Configure(null, d, true, 1, false);
                a.Move(500); b.Move(500); invisible.Move(500);
                if (reconcileOnDrop)
                {
                    d.Selection.Add(o);
                    c.Down(); o.Move(10); c.Up();
                }
                else
                {
                    m.RefreshAllObjects();
                }
                Assert(g.ObjectIDs.Contains(a.InstanceGuid) && g.ObjectIDs.Contains(b.InstanceGuid)
                    && g.ObjectIDs.Contains(child.InstanceGuid) && child.ObjectIDs.Contains(invisible.InstanceGuid)
                    && m.LastChangeCount == 0,
                    "an untracked descendant invalidated external carrying");
            });
        }
        Check("A carried group with empty descendants preserves its subtree when leaving its parent", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var empty = AddGroup(d);
            var child = AddGroup(d, a, empty);
            g.RemoveObject(a.InstanceGuid); g.AddObject(child.InstanceGuid);
            d.Selection.Add(child);
            c.Down(); a.Move(500); c.Up();
            Assert(child.ObjectIDs.Contains(a.InstanceGuid) && child.ObjectIDs.Contains(empty.InstanceGuid)
                && g.ObjectIDs.SequenceEqual(new[] { b.InstanceGuid }) && m.LastChangeCount == 1,
                "leaving the parent detached the child's members or kept the old parent link");
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
        Check("Click preserves pending external movement for Refresh", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            a.Move(500);
            d.Selection.Add(a);
            c.Down();
            c.ClickUp();
            Assert(g.ObjectIDs.SequenceEqual(new[] { a.InstanceGuid, b.InstanceGuid })
                && m.LastChangeCount == 0 && d.UndoServer.UndoCount == 0 && !d.IsModified,
                "click reconciled pending movement or recorded an edit");
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
        Check("Refresh applies external movement once without extra undo records", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            a.Move(500);
            m.RefreshAllObjects();
            Assert(g.ObjectIDs.SequenceEqual(new[] { b.InstanceGuid }) && m.LastChangeCount == 1,
                "Refresh retained the moved-out member");
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
            var added = new Obj(60);
            d.Add(added);
            d.Selection.Add(added);
            c.Up();
            Assert(!g.ObjectIDs.Contains(a.InstanceGuid) && g.ObjectIDs.Contains(added.InstanceGuid)
                && g.ObjectIDs.Contains(b.InstanceGuid),
                "placement skipped the new object or erased pending movement");
            m.RefreshAllObjects();
            Assert(g.ObjectIDs.Contains(added.InstanceGuid) && !g.ObjectIDs.Contains(a.InstanceGuid)
                && m.LastChangeCount == 0, "Refresh changed the reconciled placement");
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
        Check("GG requires two distinct taps within the time window", () =>
        {
            long now = 0;
            var (d, c, m, g, a, b, o) = Setup(shortcutTimestamp: () => now);
            d.Selection.Add(o);
            c.Press(System.Windows.Forms.Keys.G);
            now += System.Diagnostics.Stopwatch.Frequency / 10;
            c.Press(System.Windows.Forms.Keys.G);
            Assert(m.ShortcutPrompt == string.Empty, "holding G armed the command");
            c.Release(System.Windows.Forms.Keys.G);
            now += System.Diagnostics.Stopwatch.Frequency;
            c.TapG();
            Assert(m.ShortcutPrompt == string.Empty, "slow taps armed the command");
            now += System.Diagnostics.Stopwatch.Frequency / 10;
            c.TapG();
            Assert(m.ShortcutPrompt.Contains("Click group"), "quick taps did not arm the command");
            d.Selection.Clear(); d.Selection.Add(g); c.Down(); c.ClickUp();
            Assert(g.ObjectIDs.Contains(o.InstanceGuid) && m.ShortcutPrompt == string.Empty
                && m.LastChangeCount == 1 && d.UndoServer.UndoCount == 1,
                "GG addition or prompt cleanup failed");
        });
        Check("GG leaves modified keys, F6 and editor typing alone", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            d.Selection.Add(o);
            foreach (var modifier in new[] { System.Windows.Forms.Keys.Control, System.Windows.Forms.Keys.Shift, System.Windows.Forms.Keys.Alt })
            {
                var first = c.Press(System.Windows.Forms.Keys.G, modifier);
                c.Release(System.Windows.Forms.Keys.G);
                var second = c.Press(System.Windows.Forms.Keys.G, modifier);
                c.Release(System.Windows.Forms.Keys.G);
                Assert(!first.Handled && !first.SuppressKeyPress && !second.Handled
                    && m.ShortcutPrompt == string.Empty, "modified G was intercepted");
            }
            Assert(!c.Press(System.Windows.Forms.Keys.F6).Handled, "F6 is still intercepted");
            c.Blur();
            Assert(!c.Press(System.Windows.Forms.Keys.G).Handled, "typing without canvas focus was intercepted");
            c.Release(System.Windows.Forms.Keys.G); c.GG();
            Assert(m.ShortcutPrompt == string.Empty, "editor typing armed the command");
        });
        Check("GG resets between focus, mouse, other-key and disable interruptions", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            d.Selection.Add(o);
            c.TapG(); c.Press(System.Windows.Forms.Keys.H); c.TapG();
            Assert(m.ShortcutPrompt == string.Empty, "G H G armed the command");
            c.Blur(); c.Focused = true; c.TapG();
            Assert(m.ShortcutPrompt == string.Empty, "focus change retained the first tap");
            c.Down(); c.ClickUp(); c.TapG();
            Assert(m.ShortcutPrompt == string.Empty, "mouse click retained the first tap");
            m.Configure(null, d, false, 1, false);
            m.Configure(null, d, true, 1, false); c.TapG();
            Assert(m.ShortcutPrompt == string.Empty, "disable retained the first tap");
            c.TapG();
            Assert(m.ShortcutPrompt != string.Empty, "GG failed after re-enabling");
            c.Escape();
            Assert(m.ShortcutPrompt == string.Empty && m.Status == "Add to group cancelled",
                "Escape left an active prompt");
        });
        Check("GG ignores empty selection and clears the prompt after a missed destination", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            Assert(!c.Press(System.Windows.Forms.Keys.G).Handled, "G with no selection was intercepted");
            c.Release(System.Windows.Forms.Keys.G);
            d.Selection.Add(o); c.GG();
            d.Selection.Clear(); c.Down(); c.ClickUp();
            Assert(m.ShortcutPrompt == string.Empty && m.Status.Contains("cancelled")
                && d.UndoServer.UndoCount == 0, "missed destination left stale feedback or changed membership");
        });
        Check("GG does not add a group into its own descendant", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var child = new GH_Group(d);
            child.AddObject(o.InstanceGuid);
            d.Add(child);
            g.AddObject(child.InstanceGuid);
            d.Selection.Add(g);
            c.GG();
            d.Selection.Clear(); d.Selection.Add(child);
            c.Down(); c.ClickUp();
            Assert(!child.ObjectIDs.Contains(g.InstanceGuid) && m.LastChangeCount == 0,
                "GG created a cyclic group hierarchy");
        });
        Check("GG preserves unrelated pending external moves", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var x = new Obj(2000); var y = new Obj(2060);
            d.Add(x); d.Add(y);
            var target = new GH_Group(d); target.AddObject(x.InstanceGuid); target.AddObject(y.InstanceGuid); d.Add(target);
            a.Move(500);
            d.Selection.Add(o); c.GG();
            d.Selection.Clear(); d.Selection.Add(target); c.Down(); c.ClickUp();
            Assert(target.ObjectIDs.Contains(o.InstanceGuid) && m.LastChangeCount == 1,
                "GG did not add the captured object");
            m.RefreshAllObjects();
            Assert(!g.ObjectIDs.Contains(a.InstanceGuid), "GG erased unrelated pending external move; escaped member retained");
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
        Check("GG preserves pending moves through an unrelated native drag", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var target = AddGroup(d, new Obj(2000), new Obj(2060));
            a.Move(500);
            d.Selection.Add(o); c.GG();
            d.Selection.Clear(); d.Selection.Add(target); c.Down(); c.ClickUp();
            d.Selection.Clear(); d.Selection.Add(o); c.Down(); o.Move(5); c.Up();
            Assert(!g.ObjectIDs.Contains(a.InstanceGuid) && target.ObjectIDs.Contains(o.InstanceGuid),
                "GG lost pending movement or the subsequent drag undid the addition");
        });
        Check("GG into a nested child preserves pending member exits", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var sibling = new Obj(30);
            var child = AddGroup(d, a, sibling);
            g.RemoveObject(a.InstanceGuid); g.AddObject(child.InstanceGuid);
            m.RefreshAllObjects();
            var added = new Obj(20); d.Add(added);
            a.Move(500);
            d.Selection.Add(added); c.GG();
            d.Selection.Clear(); d.Selection.Add(child); c.Down(); c.ClickUp();
            m.RefreshAllObjects();
            Assert(!child.ObjectIDs.Contains(a.InstanceGuid) && child.ObjectIDs.Contains(added.InstanceGuid)
                && child.ObjectIDs.Contains(sibling.InstanceGuid) && g.ObjectIDs.Contains(child.InstanceGuid),
                "nested GG edit erased an exit or damaged parent-child membership");
        });
        Check("GG adds a subtree and later dragging it out preserves internal links", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var leaf = AddGroup(d, o);
            var root = AddGroup(d, leaf);
            d.Selection.Add(root); c.GG();
            d.Selection.Clear(); d.Selection.Add(g); c.Down(); c.ClickUp();
            Assert(g.ObjectIDs.Contains(root.InstanceGuid) && root.ObjectIDs.Contains(leaf.InstanceGuid)
                && leaf.ObjectIDs.Contains(o.InstanceGuid), "GG flattened the subtree");
            m.RefreshAllObjects();
            d.Selection.Clear(); d.Selection.Add(root); c.Down(); o.Move(500); c.Up();
            Assert(!g.ObjectIDs.Contains(root.InstanceGuid) && root.ObjectIDs.Contains(leaf.InstanceGuid)
                && leaf.ObjectIDs.Contains(o.InstanceGuid), "subtree exit damaged internal links");
        });
        Check("GG rejects a deep cycle while still adding valid selected objects", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var leaf = AddGroup(d, o);
            var middle = AddGroup(d, leaf);
            g.AddObject(middle.InstanceGuid);
            var added = new Obj(1030); d.Add(added);
            d.Selection.Add(g); d.Selection.Add(added); c.GG();
            d.Selection.Clear(); d.Selection.Add(leaf); c.Down(); c.ClickUp();
            Assert(!leaf.ObjectIDs.Contains(g.InstanceGuid) && leaf.ObjectIDs.Contains(added.InstanceGuid)
                && middle.ObjectIDs.Contains(leaf.InstanceGuid) && g.ObjectIDs.Contains(middle.InstanceGuid)
                && m.LastChangeCount == 1, "deep cycle was accepted or valid addition was lost");
        });
        Check("Escape cancels GG without changing nested memberships or undo history", () =>
        {
            var (d, c, m, g, a, b, o) = Setup();
            var child = AddGroup(d, o);
            d.Selection.Add(child); c.GG(); c.Escape();
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
