using System;
using System.Drawing;
using System.Linq;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using HopperGroup;

internal static class PolicyTests
{
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly GH_Document Document = new();
        internal readonly GH_Canvas Canvas;
        internal readonly GroupMembershipManager Manager;
        internal Fixture(Func<long> timestamp = null)
        {
            Canvas = new GH_Canvas { Document = Document };
            Instances.ActiveCanvas = Canvas;
            Manager = new GroupMembershipManager(timestamp);
        }
        internal GH_Group Group(params IGH_DocumentObject[] members)
        {
            var group = new GH_Group(Document);
            foreach (var member in members)
            {
                if (!Document.Objects.Contains(member)) Document.Add(member);
                group.AddObject(member.InstanceGuid);
            }
            Document.Add(group);
            return group;
        }
        internal void Start(float scale = 1) => Manager.Configure(null, Document, true, scale, false);
        internal void Drag(Obj obj, float distance)
        {
            Canvas.Down(); obj.Move(distance); Canvas.Up();
        }
        public void Dispose() { Manager.Dispose(); Instances.ActiveCanvas = null; }
    }

    internal static void Run(Action<string, Action> check)
    {
        check("A pending external departure cannot stretch a native member's exit reference", () =>
        {
            using var f = new Fixture();
            var a = new Obj(0); var b = new Obj(10); var c = new Obj(20);
            var group = f.Group(a, b, c); f.Start();
            b.Move(1000);
            f.Drag(a, 100);
            Assert(group.ObjectIDs.SequenceEqual(new[] { c.InstanceGuid }) && f.Manager.LastChangeCount == 2,
                "external member departure shielded the native member from removal");
        });
        foreach (var locked in new[] { false, true })
        {
            check($"GG instantly adds a mixed selection to its single selected group (locked: {locked})", () =>
            {
                using var f = new Fixture();
                var existing = new Obj(0); var group = f.Group(existing); group.NickName = locked ? "Inputs [lock]" : "Inputs";
                var added = new Obj(500); var scribble = new GH_Scribble(600);
                f.Document.Add(added); f.Document.Add(scribble); f.Start();
                f.Document.Selection.AddRange(new IGH_DocumentObject[] { group, existing, added, scribble }); f.Canvas.GG();
                Assert(group.ObjectIDs.Count == 3 && group.ObjectIDs.Contains(added.InstanceGuid)
                    && group.ObjectIDs.Contains(scribble.InstanceGuid) && f.Manager.ShortcutPrompt == "",
                    "mixed GG selection did not add immediately");
                Assert(f.Manager.LastChangeCount == 2 && f.Manager.Status == $"Last: +2 → '{group.NickName}'"
                    && f.Document.UndoServer.UndoNames.SequenceEqual(new[] { "HopperGroup membership" }),
                    "instant GG did not reuse click feedback and a single membership undo");
            });
        }
        check("GG with only one selected group still arms destination nesting", () =>
        {
            using var f = new Fixture(); var group = f.Group(new Obj()); f.Start();
            f.Document.Selection.Add(group); f.Canvas.GG();
            Assert(f.Manager.ShortcutPrompt.Length > 0 && f.Document.UndoServer.UndoCount == 0
                && !group.ObjectIDs.Contains(group.InstanceGuid), "group-only GG stopped arming or added itself");
        });
        check("GG with two selected groups and a component still arms the ambiguous selection", () =>
        {
            using var f = new Fixture(); var a = f.Group(new Obj()); var b = f.Group(new Obj(100));
            var component = new Obj(500); f.Document.Add(component); f.Start();
            f.Document.Selection.AddRange(new IGH_DocumentObject[] { a, b, component }); f.Canvas.GG();
            Assert(f.Manager.ShortcutPrompt.Contains("Add 3 object(s)") && f.Document.UndoServer.UndoCount == 0
                && !a.ObjectIDs.Contains(component.InstanceGuid) && !b.ObjectIDs.Contains(component.InstanceGuid),
                "ambiguous GG chose a group automatically");
        });
        check("Instant GG reports already-member selections without an undo record", () =>
        {
            using var f = new Fixture(); var member = new Obj(); var group = f.Group(member); group.NickName = "Inputs"; f.Start();
            f.Document.Selection.AddRange(new IGH_DocumentObject[] { group, member }); f.Canvas.GG();
            Assert(f.Manager.ShortcutPrompt == "" && f.Manager.Status == "Already in 'Inputs'"
                && f.Manager.LastChangeCount == 0 && f.Document.UndoServer.UndoCount == 0 && !f.Document.IsModified,
                "already-member GG armed or reported a change");
        });
        check("Instant GG matches additive click semantics for a component in a nested child", () =>
        {
            using var f = new Fixture(); var member = new Obj(); var child = f.Group(member);
            var parent = f.Group(child); f.Start();
            f.Document.Selection.AddRange(new IGH_DocumentObject[] { parent, member }); f.Canvas.GG();
            Assert(parent.ObjectIDs.Contains(member.InstanceGuid) && parent.ObjectIDs.Contains(child.InstanceGuid)
                && child.ObjectIDs.Contains(member.InstanceGuid) && !child.ObjectIDs.Contains(parent.InstanceGuid)
                && f.Manager.ShortcutPrompt == "", "instant GG flattened or cycled a nested group");
        });
        check("Output changes coalesce while a refresh solution is pending", () =>
        {
            using var f = new Fixture();
            var member = new Obj(10); var group = f.Group(new Obj(0), new Obj(100), member);
            var owner = new HopperGroupComponent(); f.Document.Add(owner); owner.Solve(new DataAccess());
            try
            {
                f.Document.DeferSolutions = true;
                var schedules = f.Document.ScheduleCount; var refreshes = owner.RefreshCount;
                f.Drag(member, 500); f.Drag(member, -500);
                Assert(f.Document.ScheduleCount == schedules + 1 && f.Document.ScheduledSolutions.Count == 1,
                    "pending output solution was scheduled twice");
                f.Document.ScheduledSolutions.Dequeue()(f.Document);
                Assert(owner.RefreshCount == refreshes + 1 && group.ObjectIDs.Contains(member.InstanceGuid),
                    "coalesced callback did not refresh the latest state");
                owner.ScheduleOutputRefresh();
                Assert(f.Document.ScheduleCount == schedules + 1, "unchanged state scheduled again after callback");
            }
            finally { owner.DocumentContextChanged(f.Document, GH_DocumentContext.Close); }
        });
        check("Status uses Active, Off and named GG feedback", () =>
        {
            using var f = new Fixture();
            var group = f.Group(new Obj(0), new Obj(100)); group.NickName = "Inputs";
            var incoming = new Obj(500); f.Document.Add(incoming); f.Start();
            Assert(f.Manager.Status == "Active · 1 group", "active status exposes cache internals");
            f.Document.Selection.Add(incoming); f.Canvas.GG(); f.Document.Selection.Clear();
            f.Canvas.Down(new Point(50, 5)); f.Canvas.ClickUp(new Point(50, 5));
            Assert(f.Manager.Status == "Last: +1 → 'Inputs'", "GG success lacks destination feedback");
            f.Manager.Configure(null, f.Document, false, 1, false);
            Assert(f.Manager.Status == "Off", "disabled status is not Off");
        });
        check("Unchanged outputs and repeated no-op drags schedule no additional solution", () =>
        {
            using var f = new Fixture();
            var member = new Obj(10); f.Group(new Obj(0), new Obj(100), member);
            var owner = new HopperGroupComponent(); f.Document.Add(owner); owner.Solve(new DataAccess());
            try
            {
                var schedules = f.Document.ScheduleCount;
                owner.ScheduleOutputRefresh(); owner.ScheduleOutputRefresh();
                f.Drag(member, 1); f.Drag(member, 1);
                Assert(f.Document.ScheduleCount == schedules, "unchanged outputs scheduled a redundant solution");
                f.Drag(member, 500);
                Assert(f.Document.ScheduleCount == schedules + 1, "changed membership failed to refresh outputs");
            }
            finally { owner.DocumentContextChanged(f.Document, GH_DocumentContext.Close); }
        });
        foreach (var systemWindow in new[] { 200, 1000 })
        {
            foreach (var elapsed in new[] { 650, 651, 1000, 1001 })
            {
                check($"GG honours system double-click time {systemWindow} at {elapsed}ms", () =>
                {
                    var previous = System.Windows.Forms.SystemInformation.DoubleClickTime;
                    try
                    {
                        System.Windows.Forms.SystemInformation.DoubleClickTime = systemWindow;
                        long now = 0; using var f = new Fixture(() => now);
                        var selected = new Obj(); f.Document.Add(selected); f.Document.Selection.Add(selected); f.Start();
                        f.Canvas.TapG(); now = System.Diagnostics.Stopwatch.Frequency * elapsed / 1000; f.Canvas.TapG();
                        Assert((f.Manager.ShortcutPrompt.Length > 0) == (elapsed <= Math.Max(650, systemWindow)),
                            "GG used the wrong tap window");
                    }
                    finally { System.Windows.Forms.SystemInformation.DoubleClickTime = previous; }
                });
            }
        }
        foreach (var clickObject in new[] { false, true })
        {
            check($"GG click resolves the innermost group over {(clickObject ? "a component" : "empty space")} at zoom", () =>
            {
                using var f = new Fixture();
                var member = new Obj(50); var child = f.Group(member, new Obj(80));
                var parent = f.Group(child, new Obj(-50, -50), new Obj(150, 50));
                var incoming = new Obj(500); f.Document.Add(incoming); f.Start();
                f.Document.Selection.Add(incoming); f.Canvas.GG();
                f.Document.Selection.Clear(); if (clickObject) f.Document.Selection.Add(member);
                f.Canvas.Viewport.Zoom = 2; f.Canvas.Viewport.Offset = new PointF(100, 200);
                var point = clickObject ? new Point(210, 210) : new Point(230, 210);
                f.Canvas.Down(point); f.Canvas.ClickUp(point);
                Assert(child.ObjectIDs.Contains(incoming.InstanceGuid) && !parent.ObjectIDs.Contains(incoming.InstanceGuid)
                    && f.Manager.ShortcutPrompt == "", "GG required group selection or ignored canvas coordinates");
            });
        }
        check("GG destination click can explicitly target a locked group", () =>
        {
            using var f = new Fixture();
            var locked = f.Group(new Obj(0), new Obj(100)); locked.NickName = "[lock]";
            var incoming = new Obj(500); f.Document.Add(incoming); f.Start();
            f.Document.Selection.Add(incoming); f.Canvas.GG(); f.Document.Selection.Clear();
            f.Canvas.Down(new Point(50, 5)); f.Canvas.ClickUp(new Point(50, 5));
            Assert(locked.ObjectIDs.Contains(incoming.InstanceGuid), "GG click skipped its explicit locked destination");
        });
        foreach (var refresh in new[] { false, true })
        {
            check($"[LoCk] preserves members and skips auto targets during {(refresh ? "Refresh" : "drag")}", () =>
            {
                using var f = new Fixture();
                var member = new Obj(50); var locked = f.Group(new Obj(0), new Obj(100), member);
                locked.NickName = "Inputs [LoCk]";
                var outer = f.Group(new Obj(-100, -100), new Obj(200, 100), locked);
                var incoming = new Obj(45); f.Document.Add(incoming); f.Start();
                if (refresh) { member.Move(500); incoming.Move(5); f.Manager.RefreshAllObjects(); }
                else { f.Canvas.Down(); member.Move(500); incoming.Move(5); f.Canvas.Up(); }
                Assert(locked.ObjectIDs.Contains(member.InstanceGuid) && !locked.ObjectIDs.Contains(incoming.InstanceGuid),
                    "automation edited a locked group's members");
                Assert(outer.ObjectIDs.Contains(incoming.InstanceGuid), "locked target did not fall through to eligible outer group");
            });
        }
        check("New placement skips a locked group when no eligible group contains it", () =>
        {
            using var f = new Fixture();
            var locked = f.Group(new Obj(0), new Obj(100)); locked.NickName = "[lock]"; f.Start();
            var incoming = new Obj(50); f.Document.Add(incoming); f.Document.Selection.Add(incoming);
            f.Canvas.Down(); f.Canvas.ClickUp();
            Assert(!locked.ObjectIDs.Contains(incoming.InstanceGuid) && f.Manager.LastChangeCount == 0,
                "placement joined a locked target");
        });
        check("Carrying a nested child out of a locked parent preserves the parent's membership", () =>
        {
            using var f = new Fixture();
            var member = new Obj(10); var child = f.Group(member);
            var locked = f.Group(new Obj(0), new Obj(100), child); locked.NickName = "[lock]";
            f.Start(); f.Document.Selection.Add(child); f.Drag(member, 500);
            Assert(locked.ObjectIDs.Contains(child.InstanceGuid) && child.ObjectIDs.Contains(member.InstanceGuid),
                "nested handling removed a locked membership");
        });
        check("Carrying a child into a locked outline does not nest it", () =>
        {
            using var f = new Fixture();
            var member = new Obj(500); var child = f.Group(member);
            var locked = f.Group(new Obj(0, -50), new Obj(100, 50)); locked.NickName = "[lock]";
            f.Start(); f.Document.Selection.Add(child); f.Drag(member, -450);
            Assert(!locked.ObjectIDs.Contains(child.InstanceGuid), "nested handling added to a locked group");
        });
        check("Deleting a nested group does not promote its contents into a locked parent", () =>
        {
            using var f = new Fixture();
            var member = new Obj(10); var child = f.Group(member); var locked = f.Group(child, new Obj(100));
            locked.NickName = "[lock]"; f.Start(); var before = locked.ObjectIDs.ToArray();
            f.Document.Delete(child);
            Assert(locked.ObjectIDs.SequenceEqual(before), "deletion promotion edited a locked parent");
        });
        check("Explicit GG may add to a locked group", () =>
        {
            using var f = new Fixture();
            var locked = f.Group(new Obj(0), new Obj(100)); locked.NickName = "[lock]";
            var incoming = new Obj(500); f.Document.Add(incoming); f.Start();
            f.Document.Selection.Add(incoming); f.Canvas.GG();
            f.Document.Selection.Clear(); f.Document.Selection.Add(locked); f.Canvas.GG();
            Assert(locked.ObjectIDs.Contains(incoming.InstanceGuid), "explicit GG rejected a locked group");
        });
        check("Renaming an existing group to [lock] takes effect on the next drag and Refresh", () =>
        {
            using var f = new Fixture();
            var member = new Obj(10); var group = f.Group(new Obj(0), member); f.Start();
            group.NickName = "[LOCK]"; f.Drag(member, 500); f.Manager.RefreshAllObjects();
            Assert(group.ObjectIDs.Contains(member.InstanceGuid), "renamed group was still managed");
        });
        check("An overlapping unrelated smaller group cannot steal a retained member", () =>
        {
            using var f = new Fixture();
            var moving = new Obj(80); var current = f.Group(new Obj(0, 100), new Obj(90), moving);
            var overlap = f.Group(new Obj(75), new Obj(170)); f.Start();
            f.Drag(moving, 5);
            Assert(current.ObjectIDs.Contains(moving.InstanceGuid) && !overlap.ObjectIDs.Contains(moving.InstanceGuid),
                "smallest overlapping box stole a retained member");
            f.Drag(moving, 70);
            Assert(!current.ObjectIDs.Contains(moving.InstanceGuid) && overlap.ObjectIDs.Contains(moving.InstanceGuid),
                "normal innermost targeting did not resume after exit");
        });
        check("A retained member can intentionally enter its nested child", () =>
        {
            using var f = new Fixture();
            var moving = new Obj(20); var child = f.Group(new Obj(75), new Obj(90));
            var parent = f.Group(new Obj(0, 100), new Obj(150), child, moving); f.Start();
            f.Drag(moving, 60);
            Assert(child.ObjectIDs.Contains(moving.InstanceGuid) && !parent.ObjectIDs.Contains(moving.InstanceGuid)
                && parent.ObjectIDs.Contains(child.InstanceGuid), "retention blocked a nested destination");
        });
        check("An ungrouped object still chooses the innermost overlapping group", () =>
        {
            using var f = new Fixture();
            var outer = f.Group(new Obj(0, 100), new Obj(90));
            var inner = f.Group(new Obj(75), new Obj(170));
            var moving = new Obj(80); f.Document.Add(moving); f.Start(); f.Drag(moving, 5);
            Assert(inner.ObjectIDs.Contains(moving.InstanceGuid) && !outer.ObjectIDs.Contains(moving.InstanceGuid),
                "ungrouped object lost innermost targeting");
        });
        foreach (var scale in new[] { 0f, 0.5f, 1f, 2f })
        {
            foreach (var vertical in new[] { false, true })
            {
                check($"Large panel exit buffer is capped on {(vertical ? "Y" : "X")} before scale {scale}", () =>
                {
                    using var f = new Fixture();
                    var panel = new Obj(-115, -90);
                    panel.Attributes.Bounds = new RectangleF(-115, -90, 250, 200);
                    var group = f.Group(new Obj(0), panel); f.Start(scale);
                    void Move(float distance)
                    {
                        f.Canvas.Down();
                        if (!vertical) panel.Move(distance);
                        else
                        {
                            var bounds = panel.Attributes.Bounds; bounds.Offset(0, distance); panel.Attributes.Bounds = bounds;
                            var pivot = panel.Attributes.Pivot; pivot.Y += distance; panel.Attributes.Pivot = pivot;
                        }
                        f.Canvas.Up();
                    }
                    Move(9 + 120 * scale);
                    Assert(group.ObjectIDs.Contains(panel.InstanceGuid), "panel left before the scaled cap");
                    Move(2);
                    Assert(!group.ObjectIDs.Contains(panel.InstanceGuid), "large panel retained an uncapped exit buffer");
                });
            }
        }
        foreach (var steps in new[] { 1, 3, 6 })
        {
            check($"Stationary exit reference releases the same endpoint in {steps} drag(s)", () =>
            {
                using var f = new Fixture();
                var anchor = new Obj(0); var moving = new Obj(10);
                var group = f.Group(anchor, moving); f.Start();
                for (var i = 0; i < steps; i++) f.Drag(moving, 60f / steps);
                Assert(!group.ObjectIDs.Contains(moving.InstanceGuid), "expanded outline swallowed the repeated exit");
            });
        }
        check("Eight small pulls from one unit inside the stationary outline leave the group", () =>
        {
            using var f = new Fixture();
            var moving = new Obj(14); var group = f.Group(new Obj(0), moving); f.Start();
            for (var i = 0; i < 8; i++) f.Drag(moving, 10);
            Assert(!group.ObjectIDs.Contains(moving.InstanceGuid), "small pulls stretched the exit reference");
        });
        check("Moving every member without carrying falls back to the frozen outline", () =>
        {
            using var f = new Fixture();
            var a = new Obj(0); var b = new Obj(10); var group = f.Group(a, b); f.Start();
            f.Canvas.Down(); a.Move(20); b.Move(20); f.Canvas.Up();
            Assert(group.ObjectIDs.Count == 2, "all-moving fallback lost membership");
        });
        check("Stationary exit reference follows a carried translation before repeated member drags", () =>
        {
            using var f = new Fixture();
            var anchor = new Obj(0); var moving = new Obj(10); var group = f.Group(anchor, moving); f.Start();
            f.Document.Selection.Add(group);
            f.Canvas.Down(); anchor.Move(500); moving.Move(500); f.Canvas.Up();
            f.Document.Selection.Clear();
            for (var i = 0; i < 3; i++) f.Drag(moving, 20);
            Assert(group.ObjectIDs.Contains(anchor.InstanceGuid) && !group.ObjectIDs.Contains(moving.InstanceGuid),
                "translated outline stretched or lost its stationary member");
        });
        check("Moving leaves cannot stretch the stationary reference through a nested child", () =>
        {
            using var f = new Fixture();
            var moving = new Obj(10); var child = f.Group(new Obj(0), moving);
            var parent = f.Group(child, new Obj(0)); f.Start();
            for (var i = 0; i < 8; i++) f.Drag(moving, 10);
            Assert(!child.ObjectIDs.Contains(moving.InstanceGuid) && parent.ObjectIDs.Contains(child.InstanceGuid),
                "nested reference stretched or broke the child link");
        });
    }
}
