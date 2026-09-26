using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace HopperGroup
{
    internal sealed class GroupMembershipManager : IDisposable
    {
        private readonly List<GroupRegion> _groups = new List<GroupRegion>();
        private readonly Dictionary<Guid, Guid> _parentByChild = new Dictionary<Guid, Guid>();
        private readonly Dictionary<Guid, PointF> _positionsAtMouseDown = new Dictionary<Guid, PointF>();
        private readonly Dictionary<Guid, RectangleF> _groupBoundsAtMouseDown = new Dictionary<Guid, RectangleF>();
        private readonly HashSet<Guid> _addedObjectIds = new HashSet<Guid>();
        private readonly StringBuilder _debugLog = new StringBuilder();
        private HopperGroupComponent _owner;
        private GH_Document _document;
        private GH_Canvas _canvas;
        private bool _enabled;
        private bool _debug;
        private bool _cacheDirty = true;
        private bool _handlingDrop;
        private bool _hasMouseDownSnapshot;
        private Point _mouseDownLocation;
        private float _exitScale = 1f;

        public string Status { get; private set; } = "Ready";
        public int GroupCount => _groups.Count;
        public int LastChangeCount { get; private set; }
        public string DebugLog => _debugLog.ToString();

        public void Configure(HopperGroupComponent owner, GH_Document document, bool enabled, float exitScale, bool debug)
        {
            var wasEnabled = _enabled;
            var documentChanged = _document != document;
            _owner = owner;
            _enabled = enabled;
            _exitScale = Math.Max(0f, exitScale);
            _debug = debug;

            if (documentChanged)
            {
                UnsubscribeDocument();
                ClearDragState();
                _document = document;
                SubscribeDocument();
                _cacheDirty = true;
            }

            if (_enabled)
            {
                WireCanvas();
                RebuildCacheIfNeeded();
                if (!wasEnabled || documentChanged)
                {
                    Status = $"Enabled - cached {_groups.Count} group region(s)";
                }
            }
            else
            {
                UnwireCanvas();
                ClearDragState();
                Status = "Disabled";
            }
        }

        public void RefreshAllObjects()
        {
            if (!_enabled || _document == null)
            {
                LastChangeCount = 0;
                Status = _enabled ? "No document" : "Disabled";
                return;
            }

            ProcessObjects(GetManagedObjects(_document.Objects), refreshCache: true, expireOwner: false, selectionContext: SelectionContext.Empty);
        }

        public void Dispose()
        {
            UnwireCanvas();
            UnsubscribeDocument();
            _groups.Clear();
            _parentByChild.Clear();
            ClearDragState();
            _owner = null;
            _document = null;
        }

        private void SubscribeDocument()
        {
            if (_document == null)
            {
                return;
            }

            _document.ObjectsAdded += OnObjectsAdded;
            _document.ObjectsDeleted += OnObjectsDeleted;
        }

        private void UnsubscribeDocument()
        {
            if (_document == null)
            {
                return;
            }

            _document.ObjectsAdded -= OnObjectsAdded;
            _document.ObjectsDeleted -= OnObjectsDeleted;
        }

        private void WireCanvas()
        {
            var activeCanvas = Instances.ActiveCanvas;
            if (activeCanvas == null || ReferenceEquals(activeCanvas, _canvas))
            {
                return;
            }

            UnwireCanvas();
            _canvas = activeCanvas;
            _canvas.MouseDown += OnCanvasMouseDown;
            _canvas.MouseUp += OnCanvasMouseUp;
            Log("Subscribed to active Grasshopper canvas mouse events.");
        }

        private void UnwireCanvas()
        {
            if (_canvas == null)
            {
                return;
            }

            _canvas.MouseDown -= OnCanvasMouseDown;
            _canvas.MouseUp -= OnCanvasMouseUp;
            _canvas = null;
            ClearDragState();
        }

        private void OnObjectsAdded(object sender, GH_DocObjectEventArgs e)
        {
            foreach (var obj in e.Objects.Where(obj => _enabled && obj != null && !(obj is GH_Group)))
            {
                _addedObjectIds.Add(obj.InstanceGuid);
            }

            OnObjectsChanged(e);
        }

        private void OnObjectsDeleted(object sender, GH_DocObjectEventArgs e)
        {
            foreach (var obj in e.Objects)
            {
                _addedObjectIds.Remove(obj.InstanceGuid);
                _positionsAtMouseDown.Remove(obj.InstanceGuid);
            }

            OnObjectsChanged(e);
        }

        private void OnObjectsChanged(GH_DocObjectEventArgs e)
        {
            if (e.Objects.Any(obj => obj is GH_Group))
            {
                _cacheDirty = true;
                Log("Group object added or deleted; cache marked dirty.");

                if (_enabled)
                {
                    RebuildGroupCache();
                    Status = $"Enabled - cached {_groups.Count} group region(s)";
                    _owner?.ScheduleOutputRefresh();
                }
            }
        }

        private void OnCanvasMouseDown(object sender, MouseEventArgs e)
        {
            if (!_enabled || _handlingDrop || e.Button != MouseButtons.Left || !IsCurrentCanvasDocument())
            {
                return;
            }

            RebuildGroupCache();
            _positionsAtMouseDown.Clear();
            foreach (var obj in GetManagedObjects(_document.Objects))
            {
                _positionsAtMouseDown[obj.InstanceGuid] = GetObjectCenter(obj);
            }

            _groupBoundsAtMouseDown.Clear();
            foreach (var group in _groups)
            {
                _groupBoundsAtMouseDown[group.Id] = group.Bounds;
            }

            _hasMouseDownSnapshot = true;
            _mouseDownLocation = e.Location;
            Log("Frozen group cache at drag start.");
        }

        private void OnCanvasMouseUp(object sender, MouseEventArgs e)
        {
            if (!_enabled || _handlingDrop || e.Button != MouseButtons.Left || !IsCurrentCanvasDocument())
            {
                return;
            }

            var selection = _document.SelectedObjects();
            var selectedIds = new HashSet<Guid>(selection.Select(obj => obj.InstanceGuid));
            var movedObjects = GetManagedObjects(_document.Objects)
                .Where(obj => (_addedObjectIds.Contains(obj.InstanceGuid) && selectedIds.Contains(obj.InstanceGuid))
                    || (_hasMouseDownSnapshot
                        && _positionsAtMouseDown.TryGetValue(obj.InstanceGuid, out var previous)
                        && previous != GetObjectCenter(obj)))
                .ToList();
            var selectedGroups = new HashSet<Guid>(selection.OfType<GH_Group>().Select(group => group.InstanceGuid));
            var groupMoved = _hasMouseDownSnapshot && e.Location != _mouseDownLocation
                && selectedGroups.Count > 0 && movedObjects.Count == 0
                && _groups.Any(group => selectedGroups.Contains(group.Id)
                    && _groupBoundsAtMouseDown.TryGetValue(group.Id, out var previous)
                    && GetCurrentGroupBounds(group.Group) != previous);
            var selectionContext = CreateSelectionContext(movedObjects, selectedGroups);
            ClearDragState();

            if (movedObjects.Count == 0 && !groupMoved)
            {
                return;
            }

            ProcessObjects(movedObjects, refreshCache: groupMoved, expireOwner: true, selectionContext: selectionContext);
        }

        private bool IsCurrentCanvasDocument()
        {
            return _canvas != null && ReferenceEquals(_canvas.Document, _document);
        }

        private void ClearDragState()
        {
            _positionsAtMouseDown.Clear();
            _groupBoundsAtMouseDown.Clear();
            _addedObjectIds.Clear();
            _hasMouseDownSnapshot = false;
        }

        private static RectangleF GetCurrentGroupBounds(GH_Group group)
        {
            group.ExpireCaches();
            return group.Attributes?.Bounds ?? RectangleF.Empty;
        }

        private void ProcessObjects(IList<IGH_DocumentObject> objects, bool refreshCache, bool expireOwner, SelectionContext selectionContext)
        {
            if (_handlingDrop || _document == null)
            {
                return;
            }

            try
            {
                _handlingDrop = true;
                LastChangeCount = 0;

                if (refreshCache)
                {
                    RebuildGroupCache();
                }
                else
                {
                    RebuildCacheIfNeeded();
                }

                var recordedGroups = new HashSet<Guid>();
                selectionContext = selectionContext ?? SelectionContext.Empty;
                var boundsBeforeCarriedGroupMove = selectionContext.HasCarriedGroups
                    ? _groups.ToDictionary(group => group.Id, group => group.Bounds)
                    : null;

                if (refreshCache && !selectionContext.HasCarriedGroups)
                {
                    LastChangeCount += EnsureNestedGroupHierarchy(recordedGroups);
                }

                foreach (var obj in objects)
                {
                    LastChangeCount += UpdateObjectMembership(obj, recordedGroups, selectionContext);
                }

                if (selectionContext.HasCarriedGroups)
                {
                    RebuildGroupCache();
                    BuildDesiredGroupHierarchy(boundsBeforeCarriedGroupMove, selectionContext);
                    LastChangeCount += EnsureNestedGroupHierarchy(recordedGroups);
                    RebuildGroupCache();
                }

                if (LastChangeCount > 0)
                {
                    _document.IsModified = true;
                    Instances.InvalidateCanvas();
                }

                Status = $"Enabled - cached {_groups.Count} group region(s), {LastChangeCount} change(s)";
                Log($"Processed {objects.Count} object(s), {LastChangeCount} membership change(s).");
            }
            finally
            {
                _handlingDrop = false;
            }

            if (expireOwner)
            {
                _owner?.ScheduleOutputRefresh();
            }
        }

        private void RebuildCacheIfNeeded()
        {
            if (_cacheDirty)
            {
                RebuildGroupCache();
            }
        }

        private void RebuildGroupCache()
        {
            _groups.Clear();
            _parentByChild.Clear();

            if (_document == null)
            {
                _cacheDirty = false;
                return;
            }

            foreach (var group in _document.Objects.OfType<GH_Group>())
            {
                if (TryCreateRegion(group, out var region))
                {
                    _groups.Add(region);
                }
            }

            _groups.Sort((left, right) => left.Area.CompareTo(right.Area));
            BuildDesiredGroupHierarchy();
            _cacheDirty = false;
            Log($"Rebuilt group cache with {_groups.Count} group(s).");
        }

        private bool TryCreateRegion(GH_Group group, out GroupRegion region)
        {
            region = null;

            if (group == null)
            {
                return false;
            }

            if (group.Attributes == null)
            {
                group.CreateAttributes();
            }

            group.ExpireCaches();

            var bounds = group.Attributes?.Bounds ?? RectangleF.Empty;
            if (bounds.Width <= 0f || bounds.Height <= 0f)
            {
                return false;
            }

            region = new GroupRegion(group, bounds);
            return true;
        }

        private void BuildDesiredGroupHierarchy(
            Dictionary<Guid, RectangleF> boundsBeforeMove = null,
            SelectionContext selectionContext = null)
        {
            _parentByChild.Clear();
            var effectiveBounds = _groups.ToDictionary(
                group => group.Id,
                group => boundsBeforeMove != null
                    && !selectionContext.IsCarriedGroup(group.Id)
                    && boundsBeforeMove.TryGetValue(group.Id, out var previous)
                        ? previous
                        : group.Bounds);

            foreach (var child in _groups)
            {
                var parent = _groups
                    .Where(candidate => candidate.Id != child.Id
                        && GetArea(effectiveBounds[candidate.Id]) > GetArea(effectiveBounds[child.Id]))
                    .OrderBy(candidate => GetArea(effectiveBounds[candidate.Id]))
                    .FirstOrDefault(candidate => ContainsRectangle(effectiveBounds[candidate.Id], effectiveBounds[child.Id]));

                if (parent != null)
                {
                    _parentByChild[child.Id] = parent.Id;
                }
            }
        }

        private int EnsureNestedGroupHierarchy(HashSet<Guid> recordedGroups)
        {
            var changes = 0;

            foreach (var parent in _groups)
            {
                foreach (var child in _groups)
                {
                    if (parent.Id == child.Id)
                    {
                        continue;
                    }

                    var shouldContainChild = _parentByChild.TryGetValue(child.Id, out var desiredParentId)
                        && desiredParentId == parent.Id;
                    var containsChild = parent.Group.ObjectIDs.Contains(child.Id);

                    if (shouldContainChild && !containsChild)
                    {
                        RecordGroupUndo(parent.Group, recordedGroups);
                        parent.Group.AddObject(child.Id);
                        ExpireGroup(parent.Group);
                        changes++;
                        Log($"Nested group {child.Id} inside {parent.Id}.");
                    }
                    else if (!shouldContainChild && containsChild)
                    {
                        RecordGroupUndo(parent.Group, recordedGroups);
                        parent.Group.RemoveObject(child.Id);
                        ExpireGroup(parent.Group);
                        changes++;
                        Log($"Removed stale nested group {child.Id} from {parent.Id}.");
                    }
                }
            }

            return changes;
        }

        private SelectionContext CreateSelectionContext(IList<IGH_DocumentObject> movedObjects, HashSet<Guid> selectedGroups)
        {
            if (movedObjects == null || movedObjects.Count == 0 || selectedGroups.Count == 0)
            {
                return SelectionContext.Empty;
            }

            var movedIds = new HashSet<Guid>(movedObjects.Select(obj => obj.InstanceGuid));
            var objectsById = _document.Objects.ToDictionary(obj => obj.InstanceGuid);
            var carriedGroupIds = new HashSet<Guid>();
            var completeGroups = new Dictionary<Guid, bool>();

            // Every member must move with a selected group to count as a carried group.
            foreach (var group in _groups)
            {
                if (selectedGroups.Contains(group.Id))
                {
                    CollectCarriedGroups(group.Group, movedIds, objectsById, completeGroups, carriedGroupIds);
                }
            }

            foreach (var groupId in carriedGroupIds)
            {
                Log($"Preserving carried group {groupId}.");
            }

            return carriedGroupIds.Count == 0
                ? SelectionContext.Empty
                : new SelectionContext(carriedGroupIds);
        }

        private static void CollectCarriedGroups(
            GH_Group group,
            HashSet<Guid> selectedIds,
            Dictionary<Guid, IGH_DocumentObject> objectsById,
            Dictionary<Guid, bool> completeGroups,
            HashSet<Guid> carriedGroupIds)
        {
            if (!IsCompleteSelectedGroup(group, selectedIds, objectsById, completeGroups, new HashSet<Guid>())
                || !carriedGroupIds.Add(group.InstanceGuid))
            {
                return;
            }

            foreach (var objectId in group.ObjectIDs)
            {
                if (objectsById.TryGetValue(objectId, out var obj) && obj is GH_Group childGroup)
                {
                    CollectCarriedGroups(childGroup, selectedIds, objectsById, completeGroups, carriedGroupIds);
                }
            }
        }

        private static bool IsCompleteSelectedGroup(
            GH_Group group,
            HashSet<Guid> selectedIds,
            Dictionary<Guid, IGH_DocumentObject> objectsById,
            Dictionary<Guid, bool> completeGroups,
            HashSet<Guid> visiting)
        {
            if (group == null || group.ObjectIDs.Count == 0)
            {
                return false;
            }

            var groupId = group.InstanceGuid;
            if (completeGroups.TryGetValue(groupId, out var cachedResult))
            {
                return cachedResult;
            }

            if (!visiting.Add(groupId))
            {
                completeGroups[groupId] = false;
                return false;
            }

            var isComplete = group.ObjectIDs.All(objectId =>
                IsCompleteSelectedMember(objectId, selectedIds, objectsById, completeGroups, visiting));

            visiting.Remove(groupId);
            completeGroups[groupId] = isComplete;
            return isComplete;
        }

        private static bool IsCompleteSelectedMember(
            Guid objectId,
            HashSet<Guid> selectedIds,
            Dictionary<Guid, IGH_DocumentObject> objectsById,
            Dictionary<Guid, bool> completeGroups,
            HashSet<Guid> visiting)
        {
            if (!objectsById.TryGetValue(objectId, out var obj))
            {
                return false;
            }

            return obj is GH_Group childGroup
                ? IsCompleteSelectedGroup(childGroup, selectedIds, objectsById, completeGroups, visiting)
                : IsManagedObject(obj) && selectedIds.Contains(objectId);
        }

        private int UpdateObjectMembership(IGH_DocumentObject obj, HashSet<Guid> recordedGroups, SelectionContext selectionContext)
        {
            if (!IsManagedObject(obj))
            {
                return 0;
            }

            var center = GetObjectCenter(obj);
            var target = FindInnermostContainingGroup(center);
            var currentGroups = _groups
                .Where(region => region.Group.ObjectIDs.Contains(obj.InstanceGuid))
                .ToList();

            var changes = 0;
            var retainedGroupIds = new HashSet<Guid>();

            foreach (var current in currentGroups)
            {
                var exitBounds = GetExitBounds(current.Bounds, obj.Attributes.Bounds);

                if (selectionContext.IsCarriedGroup(current.Id))
                {
                    retainedGroupIds.Add(current.Id);
                    continue;
                }

                if (target != null && current.Id == target.Id)
                {
                    retainedGroupIds.Add(current.Id);
                    continue;
                }

                var keepInsideChildSafeZone = target != null
                    && IsAncestor(target.Id, current.Id)
                    && exitBounds.Contains(center);

                if (target == null && exitBounds.Contains(center))
                {
                    retainedGroupIds.Add(current.Id);
                    continue;
                }

                if (keepInsideChildSafeZone)
                {
                    retainedGroupIds.Add(current.Id);
                    continue;
                }

                RecordGroupUndo(current.Group, recordedGroups);
                current.Group.RemoveObject(obj.InstanceGuid);
                ExpireGroup(current.Group);
                changes++;
                Log($"Removed {ObjectLabel(obj)} from group {current.Id}.");
            }

            if (target != null && retainedGroupIds.Count == 0 && !target.Group.ObjectIDs.Contains(obj.InstanceGuid))
            {
                RecordGroupUndo(target.Group, recordedGroups);
                target.Group.AddObject(obj.InstanceGuid);
                ExpireGroup(target.Group);
                changes++;
                Log($"Added {ObjectLabel(obj)} to group {target.Id}.");
            }

            return changes;
        }

        private GroupRegion FindInnermostContainingGroup(PointF point)
        {
            return _groups.FirstOrDefault(region => region.Bounds.Contains(point));
        }

        private bool IsAncestor(Guid possibleAncestor, Guid child)
        {
            var current = child;
            var guard = 0;

            while (_parentByChild.TryGetValue(current, out var parent))
            {
                if (parent == possibleAncestor)
                {
                    return true;
                }

                current = parent;
                guard++;
                if (guard > _groups.Count)
                {
                    return false;
                }
            }

            return false;
        }

        private static List<IGH_DocumentObject> GetManagedObjects(IEnumerable<IGH_DocumentObject> objects)
        {
            return objects
                .Where(IsManagedObject)
                .ToList();
        }

        private static bool IsManagedObject(IGH_DocumentObject obj)
        {
            return obj != null && !(obj is GH_Group) && obj.Attributes != null
                && obj.Attributes.Bounds.Width > 0f && obj.Attributes.Bounds.Height > 0f;
        }

        private static float GetArea(RectangleF bounds)
        {
            return bounds.Width * bounds.Height;
        }

        private static PointF GetObjectCenter(IGH_DocumentObject obj)
        {
            var bounds = obj.Attributes.Bounds;
            return new PointF(bounds.Left + bounds.Width * 0.5f, bounds.Top + bounds.Height * 0.5f);
        }

        private RectangleF GetExitBounds(RectangleF groupBounds, RectangleF dragBounds)
        {
            var exitBounds = groupBounds;
            exitBounds.Inflate(dragBounds.Width * _exitScale, dragBounds.Height * _exitScale);
            return exitBounds;
        }

        private static bool ContainsRectangle(RectangleF outer, RectangleF inner)
        {
            return outer.Left <= inner.Left && outer.Top <= inner.Top
                && outer.Right >= inner.Right && outer.Bottom >= inner.Bottom;
        }

        private void RecordGroupUndo(GH_Group group, HashSet<Guid> recordedGroups)
        {
            if (_document == null || group == null || !recordedGroups.Add(group.InstanceGuid))
            {
                return;
            }

            _document.UndoUtil.RecordGenericObjectEvent("HopperGroup membership", group);
        }

        private void ExpireGroup(GH_Group group)
        {
            group.ExpireCaches();
        }

        private void Log(string message)
        {
            if (!_debug)
            {
                return;
            }

            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
            _debugLog.AppendLine(line);
            Rhino.RhinoApp.WriteLine($"[HopperGroup] {message}");
        }

        private static string ObjectLabel(IGH_DocumentObject obj)
        {
            var name = string.IsNullOrWhiteSpace(obj.NickName) ? obj.Name : obj.NickName;
            return $"{name} ({obj.InstanceGuid})";
        }

        private sealed class GroupRegion
        {
            public GroupRegion(GH_Group group, RectangleF bounds)
            {
                Group = group;
                Id = group.InstanceGuid;
                Bounds = bounds;
                Area = bounds.Width * bounds.Height;
            }

            public GH_Group Group { get; }
            public Guid Id { get; }
            public RectangleF Bounds { get; }
            public float Area { get; }
        }

        private sealed class SelectionContext
        {
            public static readonly SelectionContext Empty = new SelectionContext(new HashSet<Guid>());

            private readonly HashSet<Guid> _carriedGroupIds;

            public SelectionContext(HashSet<Guid> carriedGroupIds)
            {
                _carriedGroupIds = carriedGroupIds;
            }

            public bool HasCarriedGroups => _carriedGroupIds.Count > 0;

            public bool IsCarriedGroup(Guid groupId)
            {
                return _carriedGroupIds.Contains(groupId);
            }
        }
    }
}
