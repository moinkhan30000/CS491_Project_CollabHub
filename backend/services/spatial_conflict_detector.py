"""
Spatial Conflict Detector
=========================
Detects potential spatial collisions between elements added on different
branches using Axis-Aligned Bounding Box (AABB) overlap tests.

Falls back to a point-proximity heuristic for elements that carry no
bounding-box data so that older snapshots remain supported.

This module is intentionally standalone: it only depends on the shared
diff_schema types and has no imports from the rest of the backend.
"""

from __future__ import annotations

from typing import List, Optional, Tuple

from schemas.diff_schema import Change, Conflict

# ---------------------------------------------------------------------------
# Tuneable thresholds
# ---------------------------------------------------------------------------

# Minimum overlap volume (in cubic Revit feet) for an AABB pair to be
# reported as a collision.  A tiny positive value filters out elements
# that merely share a face or edge.
_MIN_OVERLAP_VOLUME: float = 0.001

# Fallback point-proximity radius (Revit feet) used when one or both
# elements have no bounding-box data.
_FALLBACK_PROXIMITY_FT: float = 1.0


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

class SpatialConflictDetector:
    """
    Detects spatial collisions between newly-added elements on two branches.

    Usage::

        detector = SpatialConflictDetector()
        conflicts = detector.detect(source_adds, target_adds)
    """

    def detect(
        self,
        source_changes: List[Change],
        target_changes: List[Change],
    ) -> List[Conflict]:
        """
        Compare every pair of added elements across the two branch diffs and
        return a Conflict for each pair that overlaps in 3-D space.
        """
        source_adds = [c for c in source_changes if c.changeType == "added" and c.newData]
        target_adds = [c for c in target_changes if c.changeType == "added" and c.newData]

        if not source_adds or not target_adds:
            return []

        conflicts: List[Conflict] = []

        for s_change in source_adds:
            s_bbox = _extract_bbox(s_change.newData)

            for t_change in target_adds:
                t_bbox = _extract_bbox(t_change.newData)

                # s_change.newData -> s_data, t_change.newData -> t_data (was reversed)
                collides, method = _check_collision(s_bbox, s_change.newData, t_change.newData, t_bbox)
                if not collides:
                    continue

                s_id = s_change.repoGuid or s_change.elementId
                t_id = t_change.repoGuid or t_change.elementId

                conflicts.append(Conflict(
                    elementId=f"{s_id}|{t_id}",
                    conflictType="spatial_collision",
                    description=(
                        f"Spatial collision ({method}): "
                        f"{s_change.category} '{s_change.elementId}' from source "
                        f"overlaps with {t_change.category} '{t_change.elementId}' from target"
                    ),
                    localChange=s_change.model_dump(),
                    remoteChange=t_change.model_dump(),
                    resolutionOptions=["keep_local", "accept_remote", "keep_both"],
                ))

        return conflicts


# ---------------------------------------------------------------------------
# Internal helpers
# ---------------------------------------------------------------------------

# A bounding box is represented as two (x, y, z) tuples: (min, max).
_BBox = Tuple[Tuple[float, float, float], Tuple[float, float, float]]


def _extract_bbox(data: dict) -> Optional[_BBox]:
    """
    Pull the bounding box out of element data.
    Returns None when the data carries no bounding box.
    """
    if not data:
        return None

    geom = data.get("geometry")
    if not isinstance(geom, dict):
        return None

    bb = geom.get("boundingBox")
    if not isinstance(bb, dict):
        return None

    mn = bb.get("min")
    mx = bb.get("max")
    if not isinstance(mn, dict) or not isinstance(mx, dict):
        return None

    try:
        return (
            (float(mn["x"]), float(mn["y"]), float(mn["z"])),
            (float(mx["x"]), float(mx["y"]), float(mx["z"])),
        )
    except (KeyError, TypeError, ValueError):
        return None


def _extract_location_point(data: dict) -> Optional[Tuple[float, float, float]]:
    """
    Extract a representative (x, y, z) point from the element's location field.
    Used only as a fallback when bounding-box data is unavailable.
    """
    if not data:
        return None

    loc = data.get("location")
    if not isinstance(loc, dict):
        return None

    loc_type = loc.get("type")
    try:
        if loc_type == "point":
            pt = loc.get("point", {})
            return (float(pt["x"]), float(pt["y"]), float(pt["z"]))


        if loc_type == "curve":
            sp = loc.get("startPoint", {})
            ep = loc.get("endPoint", {})
            return (
                (float(sp["x"]) + float(ep["x"])) / 2,
                (float(sp["y"]) + float(ep["y"])) / 2,
                (float(sp["z"]) + float(ep["z"])) / 2,
            )
    except (KeyError, TypeError, ValueError):
        return None

    return None


def _aabb_overlap_volume(a: _BBox, b: _BBox) -> float:
    """
    Return the volume of the AABB intersection.
    Returns 0.0 when the boxes do not overlap.
    """
    overlap_x = min(a[1][0], b[1][0]) - max(a[0][0], b[0][0])
    overlap_y = min(a[1][1], b[1][1]) - max(a[0][1], b[0][1])
    overlap_z = min(a[1][2], b[1][2]) - max(a[0][2], b[0][2])

    if overlap_x <= 0 or overlap_y <= 0 or overlap_z <= 0:
        return 0.0

    return overlap_x * overlap_y * overlap_z


def _check_collision(
    s_bbox: Optional[_BBox],
    s_data: dict,
    t_data: dict,
    t_bbox: Optional[_BBox],
) -> Tuple[bool, str]:
    """
    Determine whether two elements collide.

    Returns a (collides: bool, method: str) tuple so the caller can
    include the detection method in the conflict description.

    Priority:
      1. Both have bounding boxes : AABB volume test
      2. One or both lack bbox : point-proximity fallback
    """
    if s_bbox is not None and t_bbox is not None:
        volume = _aabb_overlap_volume(s_bbox, t_bbox)
        return volume > _MIN_OVERLAP_VOLUME, "bounding-box overlap"

    # Fallback: point proximity
    s_pt = _extract_location_point(s_data)
    t_pt = _extract_location_point(t_data)

    if s_pt is None or t_pt is None:
        return False, "no geometry"

    dist_sq = sum((a - b) ** 2 for a, b in zip(s_pt, t_pt))
    return dist_sq < _FALLBACK_PROXIMITY_FT ** 2, "point proximity (no bbox)"