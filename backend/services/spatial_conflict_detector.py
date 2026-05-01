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

    The backend can only perform AABB (bounding box) intersection tests
    because full mesh/solid geometry is not stored server-side.  A positive
    result means the bounding boxes intersect — true solid intersection must
    be confirmed inside Revit using its geometry APIs before blocking apply.

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
        return a Conflict for each pair whose bounding boxes intersect.
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

                collides, method, overlap_volume = _check_collision(
                    s_bbox, s_change.newData, t_change.newData, t_bbox
                )
                if not collides:
                    continue

                s_id = s_change.repoGuid or s_change.elementId
                t_id = t_change.repoGuid or t_change.elementId

                # Overlap severity helps the frontend/Revit decide how serious
                # the collision is. AABB overlap can be a false positive for
                # complex shapes — true solid intersection must be verified
                # in Revit before blocking apply.
                severity = _describe_severity(overlap_volume, s_change.newData, t_change.newData)

                conflicts.append(Conflict(
                    elementId=f"{s_id}|{t_id}",
                    conflictType="spatial_collision",
                    description=(
                        f"Spatial collision ({method}): "
                        f"{s_change.category} '{s_change.elementId}' (source) "
                        f"overlaps {t_change.category} '{t_change.elementId}' (target). "
                        f"{severity}"
                    ),
                    localChange=s_change.model_dump(),
                    remoteChange=t_change.model_dump(),
                    resolutionOptions=["keep_local", "accept_remote", "keep_both"],
                ))

        return conflicts


# ---------------------------------------------------------------------------
# Internal helpers
# ---------------------------------------------------------------------------

_BBox = Tuple[Tuple[float, float, float], Tuple[float, float, float]]


def _extract_bbox(data: dict) -> Optional[_BBox]:
    """Pull the bounding box out of element newData. Returns None if absent."""
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
    """Extract a representative point for fallback proximity check."""
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


def _bbox_volume(bbox: _BBox) -> float:
    """Return the total volume of a bounding box."""
    return (
        max(0.0, bbox[1][0] - bbox[0][0]) *
        max(0.0, bbox[1][1] - bbox[0][1]) *
        max(0.0, bbox[1][2] - bbox[0][2])
    )


def _aabb_overlap_volume(a: _BBox, b: _BBox) -> float:
    """Return the volume of the AABB intersection. Returns 0.0 if no overlap."""
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
) -> Tuple[bool, str, float]:
    """
    Returns (collides, method_description, overlap_volume).

    overlap_volume is 0.0 for the fallback path since we have no bbox to
    compute it from.
    """
    if s_bbox is not None and t_bbox is not None:
        volume = _aabb_overlap_volume(s_bbox, t_bbox)
        return volume > _MIN_OVERLAP_VOLUME, "bounding-box overlap", volume

    # Fallback: point proximity (no bbox available)
    s_pt = _extract_location_point(s_data)
    t_pt = _extract_location_point(t_data)

    if s_pt is None or t_pt is None:
        return False, "no geometry", 0.0

    dist_sq = sum((a - b) ** 2 for a, b in zip(s_pt, t_pt))
    return dist_sq < _FALLBACK_PROXIMITY_FT ** 2, "point proximity (no bbox)", 0.0


def _describe_severity(
    overlap_volume: float,
    s_data: dict,
    t_data: dict,
) -> str:
    """
    Produce a human-readable severity note based on overlap volume relative
    to the smaller element's bounding box volume.

    This helps the frontend/user judge whether a Revit-side geometry check
    is worth running, or whether the AABB overlap is likely a false positive.
    """
    if overlap_volume <= 0:
        return "Verify in Revit (proximity-based detection, no volume data)."

    # Try to compute overlap as a percentage of the smaller element
    s_bbox = _extract_bbox(s_data)
    t_bbox = _extract_bbox(t_data)

    if s_bbox is not None and t_bbox is not None:
        s_vol = _bbox_volume(s_bbox)
        t_vol = _bbox_volume(t_bbox)
        smaller_vol = min(s_vol, t_vol)

        if smaller_vol > 0:
            pct = (overlap_volume / smaller_vol) * 100
            if pct >= 50:
                return (
                    f"Overlap: {overlap_volume:.3f} ft³ ({pct:.0f}% of smaller element). "
                    "High likelihood of true geometry conflict — verify in Revit."
                )
            elif pct >= 10:
                return (
                    f"Overlap: {overlap_volume:.3f} ft³ ({pct:.0f}% of smaller element). "
                    "Moderate overlap — verify in Revit."
                )
            else:
                return (
                    f"Overlap: {overlap_volume:.3f} ft³ ({pct:.0f}% of smaller element). "
                    "Minor bounding-box overlap — may be a false positive, verify in Revit."
                )

    return f"Overlap volume: {overlap_volume:.3f} ft³. Verify in Revit."