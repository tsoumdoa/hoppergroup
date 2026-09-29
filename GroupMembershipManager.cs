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
        private readonly Dictionary<Guid, RectangleF> _settledGroupBounds = new Dictionary<Guid, RectangleF>();
        private readonly Dictionary<Guid, HashSet<Guid>> _settledGroupMembers = new Dictionary<Guid, HashSet<Guid>>();
        private readonly Dictionary<Guid, PointF> _settledObjectCenters = new Dictionary<Guid, PointF>();
        private readonly HashSet<Guid> _newObjectIdsSinceSettled = new HashSet<Guid>();
        private readonly HashSet<Guid> _addedObjectIds = new HashSet<Guid>();
        private readonly HashSet<Guid> _shortcutObjectIds = new HashSet<Guid>();
        private readonly StringBuilder _debugLog = new StringBuilder();
        private HopperGroupComponent _owner;
        private GH_Document _document;
        private GH_Canvas _canvas;
        private bool _enabled;
        private bool _requestedEnabled;
        private bool _debug;
        private bool _cacheDirty = true;
        private bool _handlingDrop;
        private bool _hasMouseDownSnapshot;
        private bool _hasSettledLayout;
        private Point _mouseDownLocation;
        private int _undoCountAtMouseDown;
        private float _exitScale = 1f;

        public string Status { get; private set; } = "Ready";
        public int GroupCount => _groups.Count;
        public int LastChangeCount { get; private set; }
        public string DebugLog => _debugLog.ToString();

        public void Configure(HopperGroupComponent owner, GH_Document document, bool enabled, float exitScale, bool debug)
        {
            var wasEnabled = _enabled;
            var documentChanged = _document != document;
            if (_owner != owner)
            {
                if (_owner != null)
                {
                    _owner.ObjectChanged -= OnOwnerChanged;
                }
                _owner = owner;
                if (_owner != null)
                {
                    _owner.ObjectChanged += OnOwnerChanged;
                }
            }
            _requestedEnabled = enabled;
            _enabled = enabled && document != null && (_owner == null || !_owner.Locked);
            _exitScale = Math.Max(0f, exitScale);
            _debug = debug;

            if (documentChanged)
            {
                UnsubscribeDocument();
                ClearDragState();
                _shortcutObjectIds.Clear();
                ClearSettledLayout();
                _document = document;
                _cacheDirty = true;
            }

            if (_enabled)
            {
                if (!wasEnabled || documentChanged)
                {
                    SubscribeDocument();
                    _cacheDirty = true;
                }
                WireCanvas();
                RebuildCacheIfNeeded();
                if (!_hasSettledLayout)
                {
                    RememberSettledLayout();
                }
                if (!wasEnabled || documentChanged)
                {
                    Status = $"Enabled - cached {_groups.Count} group region(s)";
                }
            }
            else
            {
                UnsubscribeDocument();
                UnwireCanvas();
                ClearDragState();
                ClearSettledLayout();
                _groups.Clear();
                _parentByChild.Clear();
                _cacheDirty = true;
                LastChangeCount = 0;
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

            ProcessObjects(GetManagedObjects(_document.Objects), refreshCache: true, expireOwner: false,
                selectionContext: SelectionContext.Empty, reconcileExternalMoves: true);
        }

        public void Dispose()
        {
            _enabled = false;
            _requestedEnabled = false;
            UnwireCanvas();
            UnsubscribeDocument();
            _groups.Clear();
            _parentByChild.Clear();
            ClearDragState();
            ClearSettledLayout();
            if (_owner != null)
            {
                _owner.ObjectChanged -= OnOwnerChanged;
                _owner = null;
            }
            _document = null;
        }

        private void OnOwnerChanged(IGH_DocumentObject sender, GH_ObjectChangedEventArgs e)
        {
            // Grasshopper does not solve a locked component, so its own Disable
            // command must stop the event handlers without waiting for SolveInstance.
            if (e.Type == GH_ObjectEventType.Enabled)
            {
                Configure(_owner, _document, _requestedEnabled, _exitScale, _debug);
            }
        }

        private void SubscribeDocument()
        {
            if (_document == null)
            {
                return;
            }

            _document.ObjectsAdded += OnObjectsAdded;
            _document.ObjectsDeleted += OnObjectsDeleted;
            _document.UndoStateChanged += OnUndoStateChanged;
        }

        private void UnsubscribeDocument()
        {
            if (_document == null)
            {
                return;
            }

            _document.ObjectsAdded -= OnObjectsAdded;
            _document.ObjectsDeleted -= OnObjectsDeleted;
            _document.UndoStateChanged -= OnUndoStateChanged;
        }

        private void OnUndoStateChanged(object sender, GH_DocUndoEventArgs e)
        {
            if (!_enabled || _handlingDrop
                || (e.Operation != GH_UndoOperation.Undo && e.Operation != GH_UndoOperation.Redo))
            {
                return;
            }

            // The host has restored both positions and membership. Treat that layout as
            // authoritative instead of interpreting the restored positions as external moves.
            ClearDragState();
            _shortcutObjectIds.Clear();
            RebuildGroupCache();
            RememberSettledLayout();
            LastChangeCount = 0;
            Status = $"Enabled - cached {_groups.Count} group region(s)";
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
            _canvas.KeyDown += OnCanvasKeyDown;
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
            _canvas.KeyDown -= OnCanvasKeyDown;
            _canvas = null;
            _shortcutObjectIds.Clear();
            ClearDragState();
        }

        private void OnObjectsAdded(object sender, GH_DocObjectEventArgs e)
        {
            if (!_enabled)
            {
                return;
            }

            foreach (var obj in e.Objects.Where(obj => obj != null && !(obj is GH_Group)))
            {
                _addedObjectIds.Add(obj.InstanceGuid);
                if (_hasSettledLayout && IsManagedObject(obj))
                {
                    _settledObjectCenters[obj.InstanceGuid] = GetObjectCenter(obj);
                    _newObjectIdsSinceSettled.Add(obj.InstanceGuid);
                }
            }

            OnObjectsChanged(e);
        }

        private void OnObjectsDeleted(object sender, GH_DocObjectEventArgs e)
        {
            if (!_enabled)
            {
                return;
            }

            foreach (var obj in e.Objects)
            {
                _addedObjectIds.Remove(obj.InstanceGuid);
                _positionsAtMouseDown.Remove(obj.InstanceGuid);
                _settledObjectCenters.Remove(obj.InstanceGuid);
                _newObjectIdsSinceSettled.Remove(obj.InstanceGuid);
                _shortcutObjectIds.Remove(obj.InstanceGuid);
                _settledGroupBounds.Remove(obj.InstanceGuid);
                _settledGroupMembers.Remove(obj.InstanceGuid);
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
                    SyncSettledGroups();
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
            _undoCountAtMouseDown = _document.UndoServer.UndoCount;
            Log("Frozen group cache at drag start.");
        }

        private void OnCanvasMouseUp(object sender, MouseEventArgs e)
        {
            if (!_enabled || _handlingDrop || e.Button != MouseButtons.Left || !IsCurrentCanvasDocument())
            {
                return;
            }

            if (_shortcutObjectIds.Count > 0)
            {
                var targets = _document.SelectedObjects().OfType<GH_Group>().ToList();
                if (targets.Count == 1 && !_shortcutObjectIds.Contains(targets[0].InstanceGuid)
                    && _hasMouseDownSnapshot
                    && Math.Abs(e.Location.X - _mouseDownLocation.X) <= 3
                    && Math.Abs(e.Location.Y - _mouseDownLocation.Y) <= 3
                    && _groupBoundsAtMouseDown.TryGetValue(targets[0].InstanceGuid, out var originalBounds)
                    && GetCurrentGroupBounds(targets[0]) == originalBounds)
                {
                    AddShortcutSelection(targets[0]);
                    ClearDragState();
                    return;
                }
                _shortcutObjectIds.Clear();
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
            var selectionContext = CreateSelectionContext(movedObjects, selectedIds, selectedGroups);
            var nativeDragWasRecorded = _hasMouseDownSnapshot
                && _document.UndoServer.UndoCount == _undoCountAtMouseDown + 1
                && _document.UndoServer.UndoNames.FirstOrDefault() == "Drag";
            var pendingExternalIds = new HashSet<Guid>(GetManagedObjects(_document.Objects)
                .Where(obj => _settledObjectCenters.TryGetValue(obj.InstanceGuid, out var settled)
                    && settled != (_positionsAtMouseDown.TryGetValue(obj.InstanceGuid, out var atMouseDown)
                        ? atMouseDown
                        : GetObjectCenter(obj)))
                .Select(obj => obj.InstanceGuid));
            var externalSnapshot = pendingExternalIds.Count > 0 && _hasMouseDownSnapshot
                ? new MovementSnapshot(_positionsAtMouseDown, _groupBoundsAtMouseDown)
                : null;
            ClearDragState();

            if (movedObjects.Count == 0 && !groupMoved)
            {
                return;
            }

            ProcessObjects(movedObjects, refreshCache: groupMoved, expireOwner: true,
                selectionContext: selectionContext, mergeWithNativeDrag: nativeDragWasRecorded,
                reconcileExternalMoves: pendingExternalIds.Count > 0, externalObjectIds: pendingExternalIds,
                externalSnapshot: externalSnapshot);
        }

        private void OnCanvasKeyDown(object sender, KeyEventArgs e)
        {
            if (!_enabled || !IsCurrentCanvasDocument())
            {
                return;
            }

            if (e.KeyCode == Keys.Escape && _shortcutObjectIds.Count > 0)
            {
                _shortcutObjectIds.Clear();
                Status = "F6 add cancelled";
                _owner?.ScheduleOutputRefresh();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            if (e.KeyCode != Keys.F6) return;

            var selected = _document.SelectedObjects();
            var groups = selected.OfType<GH_Group>().ToList();
            if (groups.Count == 1 && _shortcutObjectIds.Count > 0)
            {
                AddShortcutSelection(groups[0]);
            }
            else
            {
                _shortcutObjectIds.Clear();
                foreach (var obj in selected.Where(obj => obj != null && obj != _owner))
                {
                    _shortcutObjectIds.Add(obj.InstanceGuid);
                }
                Status = _shortcutObjectIds.Count == 0
                    ? "Select objects before pressing F6"
                    : $"F6: select one destination group for {_shortcutObjectIds.Count} object(s)";
                _owner?.ScheduleOutputRefresh();
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private void AddShortcutSelection(GH_Group target)
        {
            var objectsById = _document.Objects.ToDictionary(obj => obj.InstanceGuid);
            var recordedGroups = new HashSet<Guid>();
            var changes = 0;
            foreach (var id in _shortcutObjectIds)
            {
                if (!objectsById.TryGetValue(id, out var obj) || obj == target || obj == _owner
                    || (obj is GH_Group child && GroupContainsDescendant(child, target.InstanceGuid, objectsById))
                    || target.ObjectIDs.Contains(id))
                {
                    continue;
                }
                RecordGroupUndo(target, recordedGroups);
                target.AddObject(id);
                changes++;
            }
            _shortcutObjectIds.Clear();
            if (changes > 0)
            {
                ExpireGroup(target);
                _document.IsModified = true;
                Instances.InvalidateCanvas();
                RebuildGroupCache();
                // Treat this like a manual membership edit. Other objects may have
                // pending external moves that still need their settled boundaries.
            }
            LastChangeCount = changes;
            Status = $"Added {changes} object(s) to group";
            _owner?.ScheduleOutputRefresh();
        }

        private static bool GroupContainsDescendant(GH_Group group, Guid id,
            Dictionary<Guid, IGH_DocumentObject> objectsById)
        {
            var visited = new HashSet<Guid>();
            var pending = new Stack<GH_Group>();
            pending.Push(group);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!visited.Add(current.InstanceGuid)) continue;
                if (current.ObjectIDs.Contains(id)) return true;
                foreach (var memberId in current.ObjectIDs)
                {
                    if (objectsById.TryGetValue(memberId, out var member) && member is GH_Group child)
                        pending.Push(child);
                }
            }
            return false;
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
            _undoCountAtMouseDown = 0;
        }

        private void ClearSettledLayout()
        {
            _settledGroupBounds.Clear();
            _settledGroupMembers.Clear();
            _settledObjectCenters.Clear();
            _newObjectIdsSinceSettled.Clear();
            _hasSettledLayout = false;
        }

        private void RememberSettledLayout()
        {
            _settledGroupBounds.Clear();
            _settledGroupMembers.Clear();
            foreach (var group in _groups)
            {
                _settledGroupBounds[group.Id] = group.Bounds;
                _settledGroupMembers[group.Id] = new HashSet<Guid>(group.Group.ObjectIDs);
            }

            _settledObjectCenters.Clear();
            foreach (var obj in GetManagedObjects(_document.Objects))
            {
                _settledObjectCenters[obj.InstanceGuid] = GetObjectCenter(obj);
            }

            _newObjectIdsSinceSettled.Clear();
            _hasSettledLayout = true;
        }

        private void SyncSettledGroups()
        {
            if (!_hasSettledLayout)
            {
                return;
            }

            var currentIds = new HashSet<Guid>(_groups.Select(group => group.Id));
            foreach (var missingId in _settledGroupBounds.Keys.Where(id => !currentIds.Contains(id)).ToList())
            {
                _settledGroupBounds.Remove(missingId);
                _settledGroupMembers.Remove(missingId);
            }

            foreach (var group in _groups)
            {
                if (!_settledGroupBounds.ContainsKey(group.Id))
                {
                    _settledGroupBounds[group.Id] = group.Bounds;
                    _settledGroupMembers[group.Id] = new HashSet<Guid>(group.Group.ObjectIDs);
                }
            }
        }

        private void RestoreSettledGroupCache(SelectionContext externalContext)
        {
            _groups.Clear();
            var objectsById = _document.Objects.ToDictionary(obj => obj.InstanceGuid);
            var boundsById = new Dictionary<Guid, RectangleF>();
            var changedGroups = new HashSet<Guid>();
            foreach (var group in _document.Objects.OfType<GH_Group>())
            {
                var bounds = GetSettledGroupBounds(group, objectsById, boundsById, changedGroups,
                    new HashSet<Guid>(), externalContext);
                if (bounds.Width > 0f && bounds.Height > 0f)
                {
                    _groups.Add(new GroupRegion(group, bounds));
                }
            }

            _groups.Sort((left, right) => left.Area.CompareTo(right.Area));
            BuildDesiredGroupHierarchy();
        }

        private RectangleF GetSettledGroupBounds(GH_Group group,
            Dictionary<Guid, IGH_DocumentObject> objectsById, Dictionary<Guid, RectangleF> boundsById,
            HashSet<Guid> changedGroups, HashSet<Guid> visiting, SelectionContext externalContext)
        {
            var id = group.InstanceGuid;
            if (boundsById.TryGetValue(id, out var cached))
            {
                return cached;
            }
            if (!visiting.Add(id))
            {
                return RectangleF.Empty;
            }

            _settledGroupMembers.TryGetValue(id, out var previousMembers);
            var changed = previousMembers == null || !previousMembers.SetEquals(group.ObjectIDs);
            RectangleF? currentContent = null;
            RectangleF? settledContent = null;
            foreach (var memberId in group.ObjectIDs)
            {
                if (!objectsById.TryGetValue(memberId, out var member) || member.Attributes == null)
                {
                    continue;
                }

                var current = member is GH_Group child ? GetCurrentGroupBounds(child) : member.Attributes.Bounds;
                var settled = current;
                if (member is GH_Group childGroup)
                {
                    var childBounds = GetSettledGroupBounds(childGroup, objectsById, boundsById,
                        changedGroups, visiting, externalContext);
                    changed |= changedGroups.Contains(memberId);
                    if (previousMembers != null && previousMembers.Contains(memberId))
                    {
                        settled = childBounds;
                    }
                }
                // For a new member, use its add-time center only when the whole group
                // translated. Otherwise preserve the manual membership at its current
                // position; Grasshopper does not report when that membership was edited.
                else if (((previousMembers != null && previousMembers.Contains(memberId))
                        || (_newObjectIdsSinceSettled.Contains(memberId)
                            && externalContext.IsCarriedGroup(id)))
                    && _settledObjectCenters.TryGetValue(memberId, out var center))
                {
                    settled.Offset(center.X - (current.Left + current.Width * 0.5f),
                        center.Y - (current.Top + current.Height * 0.5f));
                }

                if (current.Width > 0f && current.Height > 0f)
                {
                    currentContent = currentContent.HasValue ? RectangleF.Union(currentContent.Value, current) : current;
                }
                if (settled.Width > 0f && settled.Height > 0f)
                {
                    settledContent = settledContent.HasValue ? RectangleF.Union(settledContent.Value, settled) : settled;
                }
            }

            RectangleF bounds;
            if (!changed && _settledGroupBounds.TryGetValue(id, out var previousBounds))
            {
                bounds = previousBounds;
            }
            else
            {
                changedGroups.Add(id);
                bounds = GetCurrentGroupBounds(group);
                if (previousMembers != null && currentContent.HasValue && settledContent.HasValue)
                {
                    // Respect manual membership edits, while keeping existing members at
                    // their settled positions. Reusing the expanded live outline would
                    // swallow the very external move we are trying to reconcile.
                    var current = currentContent.Value;
                    var settled = settledContent.Value;
                    bounds = RectangleF.FromLTRB(
                        bounds.Left + settled.Left - current.Left,
                        bounds.Top + settled.Top - current.Top,
                        bounds.Right + settled.Right - current.Right,
                        bounds.Bottom + settled.Bottom - current.Bottom);
                }
            }

            visiting.Remove(id);
            boundsById[id] = bounds;
            return bounds;
        }

        private static RectangleF GetCurrentGroupBounds(GH_Group group)
        {
            group.ExpireCaches();
            return group.Attributes?.Bounds ?? RectangleF.Empty;
        }

        private SelectionContext CreateExternalMovementContext(HashSet<Guid> eligibleIds, MovementSnapshot snapshot)
        {
            var objectsById = _document.Objects.ToDictionary(obj => obj.InstanceGuid);
            var translations = new Dictionary<Guid, PointF?>();
            var carriedIds = new HashSet<Guid>();
            foreach (var group in _document.Objects.OfType<GH_Group>())
            {
                if (GetGroupTranslation(group, eligibleIds, objectsById, translations,
                    new HashSet<Guid>(), snapshot).HasValue)
                {
                    carriedIds.Add(group.InstanceGuid);
                }
            }

            return new SelectionContext(carriedIds);
        }

        private PointF? GetGroupTranslation(GH_Group group, HashSet<Guid> eligibleIds,
            Dictionary<Guid, IGH_DocumentObject> objectsById, Dictionary<Guid, PointF?> translations,
            HashSet<Guid> visiting, MovementSnapshot snapshot)
        {
            if (translations.TryGetValue(group.InstanceGuid, out var cached))
            {
                return cached;
            }

            if (!_settledGroupMembers.TryGetValue(group.InstanceGuid, out var settledMembers))
            {
                return null;
            }

            if (!visiting.Add(group.InstanceGuid))
            {
                return null;
            }

            PointF? translation = null;
            foreach (var id in group.ObjectIDs)
            {
                // A member added after the last settled layout did not take part in
                // the group's earlier move, even if it was added at the destination.
                if (!settledMembers.Contains(id))
                {
                    continue;
                }

                PointF? memberTranslation = null;
                if (objectsById.TryGetValue(id, out var obj))
                {
                    if (obj is GH_Group child)
                    {
                        memberTranslation = GetGroupTranslation(child, eligibleIds, objectsById,
                            translations, visiting, snapshot);
                    }
                    else if (eligibleIds.Contains(id) && IsManagedObject(obj)
                        && _settledObjectCenters.TryGetValue(id, out var previous))
                    {
                        var center = snapshot != null && snapshot.ObjectCenters.TryGetValue(id, out var atMouseDown)
                            ? atMouseDown
                            : GetObjectCenter(obj);
                        memberTranslation = new PointF(center.X - previous.X, center.Y - previous.Y);
                    }
                }

                if (!memberTranslation.HasValue || memberTranslation.Value == PointF.Empty
                    || (translation.HasValue
                        && (Math.Abs(translation.Value.X - memberTranslation.Value.X) > 0.01f
                            || Math.Abs(translation.Value.Y - memberTranslation.Value.Y) > 0.01f)))
                {
                    translation = null;
                    break;
                }

                translation = memberTranslation;
            }

            visiting.Remove(group.InstanceGuid);
            translations[group.InstanceGuid] = translation;
            return translation;
        }

        private void ProcessObjects(IList<IGH_DocumentObject> objects, bool refreshCache, bool expireOwner,
            SelectionContext selectionContext, bool mergeWithNativeDrag = false, bool reconcileExternalMoves = false,
            HashSet<Guid> externalObjectIds = null, MovementSnapshot externalSnapshot = null)
        {
            if (!_enabled || _handlingDrop || _document == null)
            {
                return;
            }

            try
            {
                _handlingDrop = true;
                LastChangeCount = 0;
                var undoCountBeforeProcessing = _document.UndoServer.UndoCount;
                var recordedGroups = new HashSet<Guid>();
                var processedIds = new HashSet<Guid>(objects.Select(obj => obj.InstanceGuid));
                selectionContext = selectionContext ?? SelectionContext.Empty;

                if (reconcileExternalMoves && _hasSettledLayout)
                {
                    var externallyMoved = GetManagedObjects(_document.Objects)
                        .Where(obj => (externalObjectIds != null && externalObjectIds.Contains(obj.InstanceGuid))
                            || (_settledObjectCenters.TryGetValue(obj.InstanceGuid, out var previous)
                                && previous != GetObjectCenter(obj)))
                        .ToList();

                    if (externallyMoved.Count > 0)
                    {
                        var nativeMovedIds = new HashSet<Guid>(objects.Select(obj => obj.InstanceGuid));
                        var movedIds = new HashSet<Guid>(externallyMoved.Select(obj => obj.InstanceGuid));
                        processedIds.UnionWith(movedIds);
                        var externalContext = CreateExternalMovementContext(externalObjectIds ?? movedIds, externalSnapshot);
                        RestoreSettledGroupCache(externalContext);
                        if (externalSnapshot != null)
                        {
                            // A coherent translation before mouse-down establishes the boundary
                            // for the subsequent native drag. Other groups keep their settled bounds.
                            for (var i = 0; i < _groups.Count; i++)
                            {
                                var group = _groups[i];
                                if (externalContext.IsCarriedGroup(group.Id)
                                    && externalSnapshot.GroupBounds.TryGetValue(group.Id, out var bounds))
                                {
                                    _groups[i] = new GroupRegion(group.Group, bounds);
                                }
                            }
                            _groups.Sort((left, right) => left.Area.CompareTo(right.Area));
                            BuildDesiredGroupHierarchy();
                        }
                        var combinedContext = new SelectionContext(new HashSet<Guid>(_groups
                            .Where(group => selectionContext.IsCarriedGroup(group.Id)
                                || externalContext.IsCarriedGroup(group.Id))
                            .Select(group => group.Id)));
                        var boundsBeforeMove = _groups.ToDictionary(group => group.Id, group => group.Bounds);
                        var destinationContext = externalSnapshot == null ? combinedContext : selectionContext;
                        foreach (var obj in externallyMoved)
                        {
                            // External carrying protects members only until a new native drag moves
                            // them individually. Carried destinations likewise freeze at mouse-down
                            // unless the group itself is being carried by the native drag.
                            var memberContext = externalSnapshot != null && nativeMovedIds.Contains(obj.InstanceGuid)
                                ? selectionContext
                                : combinedContext;
                            LastChangeCount += UpdateObjectMembership(obj, recordedGroups, memberContext, destinationContext);
                        }

                        RebuildGroupCache();
                        if (combinedContext.HasCarriedGroups)
                        {
                            BuildDesiredGroupHierarchy(boundsBeforeMove, destinationContext);
                            LastChangeCount += EnsureNestedGroupHierarchy(recordedGroups, destinationContext, boundsBeforeMove);
                            RebuildGroupCache();
                        }

                        // These objects were reconciled against their original boundaries. A second
                        // pass against expanded groups could immediately undo that decision.
                        objects = objects.Where(obj => !movedIds.Contains(obj.InstanceGuid)).ToList();
                        selectionContext = SelectionContext.Empty;
                    }
                }

                if (refreshCache)
                {
                    RebuildGroupCache();
                }
                else
                {
                    RebuildCacheIfNeeded();
                }

                var boundsBeforeCarriedGroupMove = selectionContext.HasCarriedGroups
                    ? _groups.ToDictionary(group => group.Id, group => group.Bounds)
                    : null;

                foreach (var obj in objects)
                {
                    LastChangeCount += UpdateObjectMembership(obj, recordedGroups, selectionContext);
                }

                if (selectionContext.HasCarriedGroups)
                {
                    RebuildGroupCache();
                    BuildDesiredGroupHierarchy(boundsBeforeCarriedGroupMove, selectionContext);
                    LastChangeCount += EnsureNestedGroupHierarchy(recordedGroups, selectionContext, boundsBeforeCarriedGroupMove);
                    RebuildGroupCache();
                }
                else
                {
                    RebuildGroupCache();
                }

                RememberSettledLayout();

                if (LastChangeCount > 0)
                {
                    _document.IsModified = true;
                    Instances.InvalidateCanvas();
                }

                if (recordedGroups.Count > 0
                    && _document.UndoServer.UndoCount - undoCountBeforeProcessing == recordedGroups.Count)
                {
                    _document.UndoUtil.MergeRecords(recordedGroups.Count + (mergeWithNativeDrag ? 1 : 0));
                }

                Status = $"Enabled - cached {_groups.Count} group region(s), {LastChangeCount} change(s)";
                Log($"Processed {processedIds.Count} object(s), {LastChangeCount} membership change(s).");
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

        private int EnsureNestedGroupHierarchy(HashSet<Guid> recordedGroups, SelectionContext context,
            Dictionary<Guid, RectangleF> boundsBeforeMove)
        {
            var changes = 0;
            var objectsById = _document.Objects.ToDictionary(obj => obj.InstanceGuid);
            foreach (var child in _groups.Where(group => context.IsCarriedGroup(group.Id)))
            {
                var currentParents = _groups.Where(parent => parent.Group.ObjectIDs.Contains(child.Id)).ToList();
                var hasCarriedParent = currentParents.Any(parent => context.IsCarriedGroup(parent.Id));

                RectangleF ParentBounds(GroupRegion parent) =>
                    boundsBeforeMove != null && boundsBeforeMove.TryGetValue(parent.Id, out var previous)
                        ? previous : parent.Bounds;
                // Internal subtree links travel together. Only roots acquire a new
                // destination, and an existing descendant can never become a parent.
                var target = hasCarriedParent ? null : _groups.Where(parent => parent.Id != child.Id
                        && !context.IsCarriedGroup(parent.Id)
                        && GetArea(ParentBounds(parent)) > child.Area
                        && ContainsRectangle(ParentBounds(parent), child.Bounds)
                        && !GroupContainsDescendant(child.Group, parent.Id, objectsById))
                    .OrderBy(parent => GetArea(ParentBounds(parent))).FirstOrDefault();
                var retained = false;
                foreach (var parent in currentParents)
                {
                    if (context.IsCarriedGroup(parent.Id) || parent.Id == target?.Id
                        || (GetExitBounds(ParentBounds(parent), child.Bounds).IntersectsWith(child.Bounds)
                            && (target == null || IsAncestor(target.Id, parent.Id))))
                    {
                        retained = true;
                        continue;
                    }
                    RecordGroupUndo(parent.Group, recordedGroups);
                    parent.Group.RemoveObject(child.Id);
                    ExpireGroup(parent.Group);
                    changes++;
                    Log($"Removed nested group {child.Id} from {parent.Id}.");
                }
                if (!retained && target != null && !target.Group.ObjectIDs.Contains(child.Id))
                {
                    RecordGroupUndo(target.Group, recordedGroups);
                    target.Group.AddObject(child.Id);
                    ExpireGroup(target.Group);
                    changes++;
                    Log($"Nested group {child.Id} inside {target.Id}.");
                }
            }

            return changes;
        }

        private SelectionContext CreateSelectionContext(IList<IGH_DocumentObject> movedObjects,
            HashSet<Guid> selectedIds, HashSet<Guid> selectedGroups)
        {
            if (movedObjects == null || movedObjects.Count == 0)
            {
                return SelectionContext.Empty;
            }

            var movedIds = new HashSet<Guid>(movedObjects.Select(obj => obj.InstanceGuid));
            var objectsById = _document.Objects.ToDictionary(obj => obj.InstanceGuid);
            var carriedGroupIds = new HashSet<Guid>();
            var completeGroups = new Dictionary<Guid, bool>();

            // A selected group whose entire contents moved is carried by Grasshopper.
            foreach (var group in _groups)
            {
                if (selectedGroups.Contains(group.Id))
                {
                    CollectCarriedGroups(group.Group, movedIds, objectsById, completeGroups, carriedGroupIds);
                }
            }

            // Dragging a component moves the selected components, even when Grasshopper
            // does not include their group in SelectedObjects. Keep a group together
            // when a strict majority of its components moved by the same amount.
            foreach (var group in _groups)
            {
                if (HasMovingSelectedMajority(group.Group, selectedIds, movedIds, objectsById))
                {
                    carriedGroupIds.Add(group.Id);
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

        private bool HasMovingSelectedMajority(GH_Group group, HashSet<Guid> selectedIds,
            HashSet<Guid> movedIds, Dictionary<Guid, IGH_DocumentObject> objectsById)
        {
            var members = new HashSet<Guid>();
            CollectLeafMembers(group, objectsById, new HashSet<Guid>(), members);
            if (members.Count < 2)
            {
                return false;
            }

            var translations = new List<PointF>();
            foreach (var id in members)
            {
                if (!selectedIds.Contains(id) || !movedIds.Contains(id)
                    || !_positionsAtMouseDown.TryGetValue(id, out var previous)
                    || !objectsById.TryGetValue(id, out var obj))
                {
                    continue;
                }

                var current = GetObjectCenter(obj);
                translations.Add(new PointF(current.X - previous.X, current.Y - previous.Y));
            }

            if (translations.Count <= members.Count / 2)
            {
                return false;
            }

            return translations.Any(translation => translations.Count(other =>
                Math.Abs(translation.X - other.X) <= 0.01f
                && Math.Abs(translation.Y - other.Y) <= 0.01f) > members.Count / 2);
        }

        private static void CollectLeafMembers(GH_Group group,
            Dictionary<Guid, IGH_DocumentObject> objectsById, HashSet<Guid> visitedGroups,
            HashSet<Guid> members)
        {
            if (!visitedGroups.Add(group.InstanceGuid))
            {
                return;
            }

            foreach (var id in group.ObjectIDs)
            {
                if (!objectsById.TryGetValue(id, out var obj))
                {
                    continue;
                }

                if (obj is GH_Group child)
                {
                    CollectLeafMembers(child, objectsById, visitedGroups, members);
                }
                else if (IsManagedObject(obj))
                {
                    members.Add(id);
                }
            }
        }

        private int UpdateObjectMembership(IGH_DocumentObject obj, HashSet<Guid> recordedGroups,
            SelectionContext selectionContext, SelectionContext destinationContext = null)
        {
            if (!IsManagedObject(obj))
            {
                return 0;
            }

            var center = GetObjectCenter(obj);
            var target = FindInnermostContainingGroup(center, destinationContext ?? selectionContext);
            var currentGroups = _groups
                .Where(region => region.Group.ObjectIDs.Contains(obj.InstanceGuid))
                .ToList();

            var changes = 0;
            var retainedMembership = false;

            foreach (var current in currentGroups)
            {
                var exitBounds = GetExitBounds(current.Bounds, obj.Attributes.Bounds);
                if (selectionContext.IsCarriedGroup(current.Id)
                    || (target != null && current.Id == target.Id)
                    || (exitBounds.Contains(center)
                        && (target == null || IsAncestor(target.Id, current.Id))))
                {
                    retainedMembership = true;
                    continue;
                }

                RecordGroupUndo(current.Group, recordedGroups);
                current.Group.RemoveObject(obj.InstanceGuid);
                ExpireGroup(current.Group);
                changes++;
                Log($"Removed {ObjectLabel(obj)} from group {current.Id}.");
            }

            if (target != null && !retainedMembership && !target.Group.ObjectIDs.Contains(obj.InstanceGuid))
            {
                RecordGroupUndo(target.Group, recordedGroups);
                target.Group.AddObject(obj.InstanceGuid);
                ExpireGroup(target.Group);
                changes++;
                Log($"Added {ObjectLabel(obj)} to group {target.Id}.");
            }

            return changes;
        }

        private GroupRegion FindInnermostContainingGroup(PointF point, SelectionContext selectionContext)
        {
            if (!selectionContext.HasCarriedGroups)
            {
                return _groups.FirstOrDefault(region => region.Bounds.Contains(point));
            }

            // A carried group receives new members at its destination, not its old footprint.
            return _groups
                .Select(region => selectionContext.IsCarriedGroup(region.Id)
                    ? new GroupRegion(region.Group, GetCurrentGroupBounds(region.Group))
                    : region)
                .Where(region => region.Bounds.Contains(point))
                .OrderBy(region => region.Area)
                .FirstOrDefault();
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
            // Small components need a usable canvas-space buffer too. Keep Exit Scale
            // as the user's adjustment for both the size-based and minimum distances.
            exitBounds.Inflate(Math.Max(dragBounds.Width * 1.5f, 40f) * _exitScale,
                Math.Max(dragBounds.Height * 1.5f, 40f) * _exitScale);
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

        private sealed class MovementSnapshot
        {
            public MovementSnapshot(Dictionary<Guid, PointF> objectCenters, Dictionary<Guid, RectangleF> groupBounds)
            {
                ObjectCenters = new Dictionary<Guid, PointF>(objectCenters);
                GroupBounds = new Dictionary<Guid, RectangleF>(groupBounds);
            }

            public Dictionary<Guid, PointF> ObjectCenters { get; }
            public Dictionary<Guid, RectangleF> GroupBounds { get; }
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
