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
        internal Fixture()
        {
            Canvas = new GH_Canvas { Document = Document };
            Instances.ActiveCanvas = Canvas;
            Manager = new GroupMembershipManager();
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
