using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using HopperGroup;

internal static class RobustnessTests
{
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static bool HasSnapshot(GroupMembershipManager manager) =>
        (bool)typeof(GroupMembershipManager).GetField("_hasMouseDownSnapshot", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);

    private sealed class Fixture : IDisposable
    {
        internal readonly GH_Document Document = new();
        internal readonly GH_Canvas Canvas;
        internal readonly Obj A = new(0), B = new(60);
        internal readonly GH_Group Group;
        internal Fixture()
        {
            Group = new GH_Group(Document);
            Document.Objects.AddRange(new IGH_DocumentObject[] { A, B, Group });
            Group.AddObject(A.InstanceGuid); Group.AddObject(B.InstanceGuid);
            Canvas = new GH_Canvas { Document = Document };
            Instances.ActiveCanvas = Canvas;
        }
        internal HopperGroupComponent Add(bool locked = false)
        {
            var component = new HopperGroupComponent { Locked = locked };
            Document.Add(component);
            return component;
        }
        public void Dispose()
        {
            foreach (var component in Document.Objects.OfType<HopperGroupComponent>().ToArray())
                component.DocumentContextChanged(Document, GH_DocumentContext.Close);
            Canvas.ChangeDocument(null);
            Instances.ActiveCanvas = null;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ClosedDocument(GH_Canvas canvas, GH_DocumentContext context)
    {
        var document = new GH_Document();
        canvas.ChangeDocument(document);
        var first = new HopperGroupComponent();
        var second = new HopperGroupComponent();
        document.Add(first); document.Add(second);
        first.DocumentContextChanged(document, context);
        second.DocumentContextChanged(document, context);
        Assert(document.HandlerCount == 0 && canvas.HandlerCount == 0, "close retained document/canvas handlers");
        canvas.ChangeDocument(null);
        return new WeakReference(document);
    }

    internal static void Run(Action<string, Action> check)
    {
        foreach (var context in new[] { GH_DocumentContext.Close, GH_DocumentContext.Unloaded })
        {
            check($"{context} detaches all handlers and allows five documents to be collected", () =>
            {
                var canvas = new GH_Canvas();
                Instances.ActiveCanvas = canvas;
                var documents = Enumerable.Range(0, 5).Select(_ => ClosedDocument(canvas, context)).ToArray();
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Assert(documents.All(reference => !reference.IsAlive), "a closed document remained rooted");
                GC.KeepAlive(canvas);
                Instances.ActiveCanvas = null;
            });
            check($"{context} followed by Loaded resumes once and uses fresh drag state", () =>
            {
                using var f = new Fixture();
                var component = f.Add();
                f.Document.Selection.Add(f.A); f.Canvas.GG(); f.Canvas.Down();
                component.DocumentContextChanged(f.Document, context);
                component.DocumentContextChanged(f.Document, context);
                component.Locked = true; component.Locked = false;
                component.Solve(new DataAccess());
                Assert(f.Canvas.HandlerCount == 0 && f.Document.HandlerCount == 0, "solve/unlock restarted an unloaded session");
                component.DocumentContextChanged(f.Document, GH_DocumentContext.Loaded);
                component.DocumentContextChanged(f.Document, GH_DocumentContext.Loaded);
                Assert(f.Canvas.HandlerCount == 7 && f.Document.HandlerCount == 3
                    && component.Manager.ShortcutPrompt == "" && !HasSnapshot(component.Manager), "reload duplicated handlers or restored transient state");
                f.Canvas.Down(); f.A.Move(500); f.Canvas.Up();
                Assert(!f.Group.ObjectIDs.Contains(f.A.InstanceGuid), "reopened manager ignored the next drag");
            });
        }

        check("Repeated Configure and Dispose are idempotent and Configure can restart", () =>
        {
            using var f = new Fixture();
            var manager = new GroupMembershipManager();
            for (var i = 0; i < 3; i++) manager.Configure(null, f.Document, true, 1, false);
            Assert(f.Canvas.HandlerCount == 7 && f.Document.HandlerCount == 3, "Configure duplicated subscriptions");
            manager.Dispose(); manager.Dispose();
            Assert(f.Canvas.HandlerCount == 0 && f.Document.HandlerCount == 0, "Dispose retained subscriptions");
            manager.Configure(null, f.Document, true, 1, false);
            f.Canvas.Down(); f.A.Move(500); f.Canvas.Up();
            Assert(!f.Group.ObjectIDs.Contains(f.A.InstanceGuid), "Configure could not restart a disposed manager");
            manager.Dispose();
        });

        check("Close still unwires everything when clearing a GG prompt cannot invalidate the canvas", () =>
        {
            using var f = new Fixture();
            var component = f.Add();
            f.Document.Selection.Add(f.A); f.Canvas.GG();
            Instances.InvalidateError = new InvalidOperationException("Injected canvas teardown failure");
            try
            {
                component.DocumentContextChanged(f.Document, GH_DocumentContext.Close);
                Assert(f.Canvas.HandlerCount == 0 && f.Document.HandlerCount == 0
                    && component.Manager.ShortcutPrompt == "" && component.Manager.DebugLog.Contains("Injected canvas teardown failure"),
                    "prompt invalidation interrupted cleanup or swallowed its error");
            }
            finally { Instances.InvalidateError = null; }
        });

        check("Duplicate object and group ids keep the first object and clear the drag snapshot", () =>
        {
            using var f = new Fixture();
            var component = f.Add();
            component.Solve(new DataAccess { Debug = true });
            var original = new Obj(9000);
            f.Document.Add(original);
            var duplicate = new Obj(0) { InstanceGuid = original.InstanceGuid };
            f.Document.Add(duplicate);
            var duplicateGroup = new GH_Group(f.Document) { InstanceGuid = f.Group.InstanceGuid };
            duplicateGroup.AddObject(f.B.InstanceGuid);
            f.Document.Add(duplicateGroup);
            f.Canvas.Down(); duplicate.Move(30); f.Canvas.Up();
            Assert(f.Group.ObjectIDs.Contains(f.A.InstanceGuid) && component.Manager.LastChangeCount == 0
                && component.Manager.GroupCount == 1 && !HasSnapshot(component.Manager), "duplicate overwrote the first object or retained drag state");
            Assert(component.Manager.DebugLog.Contains("Duplicate object id"), "duplicate ids were not logged in debug mode");
            f.Canvas.Down(); f.A.Move(500); f.Canvas.Up();
            Assert(!f.Group.ObjectIDs.Contains(f.A.InstanceGuid) && !HasSnapshot(component.Manager), "duplicate id broke subsequent mouse-up processing: " + component.Manager.DebugLog);
            f.Document.Selection.Add(f.A); f.Canvas.GG();
            f.Document.Selection.Clear(); f.Document.Selection.Add(f.Group); f.Canvas.GG();
            Assert(f.Group.ObjectIDs.Contains(f.A.InstanceGuid), "duplicate id broke shortcut membership lookup");
        });

        check("A mouse-up exception resets drag and GG state, logs with Debug off, and permits the next drag", () =>
        {
            using var f = new Fixture();
            var component = f.Add();
            var history = Rhino.RhinoApp.Lines.Count;
            f.Document.Selection.Add(f.A); f.Canvas.GG(); f.Canvas.Down();
            f.Document.SelectionError = new InvalidOperationException("Injected mouse-up selection failure");
            f.Canvas.Up();
            Assert(!HasSnapshot(component.Manager) && component.Manager.ShortcutPrompt == ""
                && component.Manager.Status == "Error - see Log", "failure retained transient drag/GG state");
            f.Canvas.Down(); f.Canvas.Up();
            Assert(component.Manager.DebugLog.Contains("OnCanvasMouseUp")
                && component.Manager.DebugLog.Contains("Injected mouse-up selection failure")
                && Rhino.RhinoApp.Lines.Count == history + 1, "errors were missing from Log/history or history repeated the same error");
            f.Document.SelectionError = null;
            f.Canvas.Down(); f.A.Move(500); f.Canvas.Up();
            Assert(!f.Group.ObjectIDs.Contains(f.A.InstanceGuid), "manager did not recover after the failed event");
        });

        check("A document ObjectsAdded exception is contained and later objects can be dragged", () =>
        {
            using var f = new Fixture();
            var component = f.Add();
            f.Document.Selection.Add(f.A); f.Canvas.GG(); f.Canvas.Down();
            var broken = new Obj();
            broken.Attributes.Compute = () => throw new InvalidOperationException("Injected object layout failure");
            f.Document.Add(broken);
            Assert(component.Manager.DebugLog.Contains("OnObjectsAdded") && !HasSnapshot(component.Manager)
                && component.Manager.ShortcutPrompt == "", "document event failure escaped or retained transient state");
            f.Document.Delete(broken);
            f.Canvas.Down(); f.A.Move(500); f.Canvas.Up();
            Assert(!f.Group.ObjectIDs.Contains(f.A.InstanceGuid), "document handler failure broke the next drag");
        });

        check("Output scheduling failures are logged and scheduled callback failures cannot recurse", () =>
        {
            using var f = new Fixture();
            var component = f.Add();
            component.Solve(new DataAccess { Debug = true });
            component.Manager.RefreshAllObjects(); // Change Log so an output refresh is needed.
            f.Document.ScheduleError = new InvalidOperationException("Injected schedule failure");
            component.ScheduleOutputRefresh();
            Assert(component.Manager.DebugLog.Contains("ScheduleOutputRefresh: InvalidOperationException: Injected schedule failure"), "scheduling catch swallowed the failure");
            f.Document.ScheduleError = null;
            component.ExpireError = new InvalidOperationException("Injected output callback failure");
            component.ScheduleOutputRefresh();
            Assert(component.Manager.DebugLog.Contains("Scheduled output refresh") && !HasSnapshot(component.Manager), "scheduled callback failure escaped");
            component.ExpireError = null;
            var count = component.RefreshCount;
            component.ScheduleOutputRefresh();
            Assert(component.RefreshCount == count + 1, "scheduled refresh failed to recover");
        });

        check("Three component instances wire one controller and inactive outputs carry a Remark", () =>
        {
            using var f = new Fixture();
            var first = f.Add(); var second = f.Add(); var third = f.Add();
            var output = new DataAccess();
            second.Solve(output); third.Solve(new DataAccess()); first.Solve(new DataAccess());
            Assert(f.Canvas.HandlerCount == 7 && f.Document.HandlerCount == 3, "multiple instances duplicated canvas/document handlers");
            Assert((string)output.Outputs[0] == "Inactive (another HopperGroup is active)"
                && second.RuntimeMessages.SequenceEqual(new[] { "Inactive: another HopperGroup component controls this document" }), "inactive status or runtime Remark missing");
            var firstRefreshes = first.RefreshCount;
            var secondRefreshes = second.RefreshCount;
            f.Canvas.Down(); f.A.Move(500); f.Canvas.Up();
            Assert(first.Manager.LastChangeCount == 1 && second.Manager.LastChangeCount == 0
                && third.Manager.LastChangeCount == 0 && first.RefreshCount > firstRefreshes
                && second.RefreshCount == secondRefreshes, "inactive instances processed a drag");
        });

        foreach (var hostDisable in new[] { false, true })
        {
            check($"Ownership passes on disable and the incumbent keeps control after re-enable (host Disable: {hostDisable})", () =>
            {
                using var f = new Fixture();
                var first = f.Add(); var second = f.Add(); var third = f.Add();
                f.Document.Selection.Add(f.A); f.Canvas.GG(); f.Canvas.Down();
                var secondRefreshes = second.RefreshCount;
                if (hostDisable) first.Locked = true;
                else first.Solve(new DataAccess { Enabled = false });
                Assert(f.Canvas.HandlerCount == 7 && f.Document.HandlerCount == 3
                    && second.Manager.GroupCount == 1 && first.Manager.ShortcutPrompt == ""
                    && !HasSnapshot(first.Manager) && second.RefreshCount > secondRefreshes
                    && second.RuntimeMessages.Count == 0, "disable failed to hand over cleanly and refresh the new owner");
                if (hostDisable) first.Locked = false;
                else first.Solve(new DataAccess());
                f.Canvas.Down(); f.A.Move(500); f.Canvas.Up();
                Assert(second.Manager.LastChangeCount == 1 && first.Manager.LastChangeCount == 0
                    && third.Manager.LastChangeCount == 0, "re-enable preempted the incumbent or duplicate work ran");
                second.Locked = true;
                Assert(first.Manager.GroupCount == 1 && third.Manager.GroupCount == 0, "next eligible registration was not chosen deterministically");
            });
        }

        check("Delete and repeated Undo/Redo hand ownership over without duplicate subscriptions", () =>
        {
            using var f = new Fixture();
            var first = f.Add(); var second = f.Add(); var third = f.Add();
            var undo = f.Document.DeleteWithUndo("Delete HopperGroup", first);
            for (var cycle = 0; cycle < 2; cycle++)
            {
                Assert(second.Manager.GroupCount == 1 && f.Canvas.HandlerCount == 7 && f.Document.HandlerCount == 3, "deletion did not activate the next instance");
                undo.Undo(f.Document); f.Document.RaiseUndoStateChanged(GH_UndoOperation.Undo);
                Assert(first.Manager.GroupCount == 0 && second.Manager.GroupCount == 1
                    && f.Canvas.HandlerCount == 7 && f.Document.HandlerCount == 3, "undo preempted the owner or duplicated subscriptions");
                undo.Redo(f.Document); f.Document.RaiseUndoStateChanged(GH_UndoOperation.Redo);
            }
            f.Document.Delete(second);
            Assert(third.Manager.GroupCount == 1, "deleting the second owner did not activate the third instance");
        });

        check("Undoing deletion of the sole component restores a working session", () =>
        {
            using var f = new Fixture();
            var component = f.Add();
            var undo = f.Document.DeleteWithUndo("Delete HopperGroup", component);
            Assert(f.Canvas.HandlerCount == 0 && f.Document.HandlerCount == 0, "sole deletion retained a session");
            undo.Undo(f.Document); f.Document.RaiseUndoStateChanged(GH_UndoOperation.Undo);
            Assert(f.Canvas.HandlerCount == 7 && f.Document.HandlerCount == 3, "undo failed to restore the sole controller");
            f.Canvas.Down(); f.A.Move(500); f.Canvas.Up();
            Assert(!f.Group.ObjectIDs.Contains(f.A.InstanceGuid), "restored component ignored a drag");
        });

        check("Locked and Enabled-false instances wait until eligible, and no owner means no handlers", () =>
        {
            using var f = new Fixture();
            var first = f.Add(locked: true); var second = f.Add();
            second.Solve(new DataAccess { Enabled = false });
            Assert(f.Canvas.HandlerCount == 0 && f.Document.HandlerCount == 0, "no eligible owner retained handlers");
            first.Locked = false;
            Assert(first.Manager.GroupCount == 1 && second.Manager.GroupCount == 0
                && f.Canvas.HandlerCount == 7 && f.Document.HandlerCount == 3, "unlock did not choose an eligible waiting instance");
        });

        check("Only the owner's Exit Scale and Refresh input apply, including after handover", () =>
        {
            using var f = new Fixture();
            var first = f.Add(); var second = f.Add();
            first.Solve(new DataAccess { ExitScale = 10 });
            second.Solve(new DataAccess { ExitScale = 0 });
            f.Document.Selection.Add(f.A);
            f.Canvas.Down(); f.A.Move(-20); f.Canvas.Up();
            Assert(f.Group.ObjectIDs.Contains(f.A.InstanceGuid), "inactive zero Exit Scale overrode the owner");
            f.A.Move(-500);
            var expirations = f.Group.CacheExpirations;
            second.Solve(new DataAccess { ExitScale = 0, Refresh = true });
            Assert(f.Group.CacheExpirations == expirations && f.Group.ObjectIDs.Contains(f.A.InstanceGuid), "inactive Refresh scanned or edited groups");
            f.A.Move(500);
            first.Solve(new DataAccess { Enabled = false });
            Assert(second.Manager.GroupCount == 1, "settings test failed to hand over");
            f.Canvas.Down(); f.A.Move(-30); f.Canvas.Up();
            Assert(!f.Group.ObjectIDs.Contains(f.A.InstanceGuid), "new owner did not use its own zero Exit Scale");
        });
    }
}
