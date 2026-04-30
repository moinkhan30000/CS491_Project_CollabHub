using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace RevitVersionControl.Services
{
    public class GhostBuildResult
    {
        public ElementId DirectShapeId { get; set; }
        public BoundingBoxXYZ BoundingBox { get; set; }
        public bool Skipped { get; set; }
        public string SkipReason { get; set; }
    }

    public class GhostBuilder
    {
        public const string CommentsTagPrefix = "RVCS_DIFF";

        private readonly Document _doc;

        public GhostBuilder(Document doc)
        {
            _doc = doc;
        }

        public GhostBuildResult BuildFromSnapshotElement(
            JObject elementData,
            Guid sessionId,
            DiffChangeKind kind,
            ElementId materialId,
            string sourceRepoGuid)
        {
            if (elementData == null)
                return new GhostBuildResult { Skipped = true, SkipReason = "no element data" };

            var bbox = ReadBoundingBox(elementData);
            if (bbox == null)
                return new GhostBuildResult { Skipped = true, SkipReason = "no bounding box geometry" };

            try
            {
                IList<GeometryObject> geometry = BuildBoxGeometry(bbox.Min, bbox.Max, materialId);
                if (geometry == null || geometry.Count == 0)
                    return new GhostBuildResult { Skipped = true, SkipReason = "geometry build failed" };

                var ds = DirectShape.CreateElement(_doc, new ElementId(BuiltInCategory.OST_GenericModel));
                if (ds == null)
                    return new GhostBuildResult { Skipped = true, SkipReason = "DirectShape creation failed" };

                ds.SetShape(geometry);
                ds.Name = $"RVCS Diff Ghost ({kind})";

                TagGhost(ds, sessionId, kind, sourceRepoGuid);

                return new GhostBuildResult
                {
                    DirectShapeId = ds.Id,
                    BoundingBox = bbox
                };
            }
            catch (Exception ex)
            {
                return new GhostBuildResult { Skipped = true, SkipReason = ex.Message };
            }
        }

        private static void TagGhost(DirectShape ds, Guid sessionId, DiffChangeKind kind, string sourceRepoGuid)
        {
            try
            {
                var commentsParam = ds.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (commentsParam != null && !commentsParam.IsReadOnly)
                {
                    string tag = $"{CommentsTagPrefix}|{sessionId:D}|{kind.ToString().ToLowerInvariant()}|{sourceRepoGuid ?? string.Empty}";
                    commentsParam.Set(tag);
                }
            }
            catch
            {
                // Ignore tagging failure; cleanup-by-name will skip but session-isolated rebuild still works.
            }
        }

        public static bool IsTaggedGhost(Element element, out Guid sessionId, out string changeType, out string sourceRepoGuid)
        {
            sessionId = Guid.Empty;
            changeType = null;
            sourceRepoGuid = null;

            if (!(element is DirectShape ds))
                return false;

            try
            {
                var commentsParam = ds.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                string raw = commentsParam?.AsString();
                if (string.IsNullOrEmpty(raw) || !raw.StartsWith(CommentsTagPrefix + "|", StringComparison.Ordinal))
                    return false;

                var parts = raw.Split('|');
                if (parts.Length < 3) return false;

                if (!Guid.TryParse(parts[1], out sessionId))
                    return false;

                changeType = parts[2];
                sourceRepoGuid = parts.Length >= 4 ? parts[3] : null;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static IList<GeometryObject> BuildBoxGeometry(XYZ min, XYZ max, ElementId materialId)
        {
            // Guard against zero-thickness boxes by inflating slightly so the ghost remains visible.
            const double minSize = 0.05; // ~0.6 inch in feet
            double dx = max.X - min.X; if (dx < minSize) { double pad = (minSize - dx) / 2.0; min = new XYZ(min.X - pad, min.Y, min.Z); max = new XYZ(max.X + pad, max.Y, max.Z); }
            double dy = max.Y - min.Y; if (dy < minSize) { double pad = (minSize - dy) / 2.0; min = new XYZ(min.X, min.Y - pad, min.Z); max = new XYZ(max.X, max.Y + pad, max.Z); }
            double dz = max.Z - min.Z; if (dz < minSize) { double pad = (minSize - dz) / 2.0; min = new XYZ(min.X, min.Y, min.Z - pad); max = new XYZ(max.X, max.Y, max.Z + pad); }

            // 8 corners of the box.
            XYZ p000 = new XYZ(min.X, min.Y, min.Z);
            XYZ p100 = new XYZ(max.X, min.Y, min.Z);
            XYZ p110 = new XYZ(max.X, max.Y, min.Z);
            XYZ p010 = new XYZ(min.X, max.Y, min.Z);
            XYZ p001 = new XYZ(min.X, min.Y, max.Z);
            XYZ p101 = new XYZ(max.X, min.Y, max.Z);
            XYZ p111 = new XYZ(max.X, max.Y, max.Z);
            XYZ p011 = new XYZ(min.X, max.Y, max.Z);

            var builder = new TessellatedShapeBuilder();
            builder.OpenConnectedFaceSet(true);

            AddFace(builder, materialId, p000, p010, p110, p100); // bottom (z = min, normal -Z)
            AddFace(builder, materialId, p001, p101, p111, p011); // top    (z = max, normal +Z)
            AddFace(builder, materialId, p000, p100, p101, p001); // front  (y = min, normal -Y)
            AddFace(builder, materialId, p010, p011, p111, p110); // back   (y = max, normal +Y)
            AddFace(builder, materialId, p000, p001, p011, p010); // left   (x = min, normal -X)
            AddFace(builder, materialId, p100, p110, p111, p101); // right  (x = max, normal +X)

            builder.CloseConnectedFaceSet();
            builder.Target = TessellatedShapeBuilderTarget.AnyGeometry;
            builder.Fallback = TessellatedShapeBuilderFallback.Mesh;

            try { builder.Build(); } catch { return null; }

            var result = builder.GetBuildResult();
            return result.GetGeometricalObjects();
        }

        private static void AddFace(TessellatedShapeBuilder builder, ElementId materialId, params XYZ[] vertices)
        {
            try
            {
                var face = new TessellatedFace(vertices, materialId ?? ElementId.InvalidElementId);
                builder.AddFace(face);
            }
            catch
            {
                // Skip the face if Revit refuses (degenerate vertices, etc.).
            }
        }

        private static BoundingBoxXYZ ReadBoundingBox(JObject elementData)
        {
            try
            {
                var geometry = elementData["geometry"] as JObject;
                var bbox = geometry?["boundingBox"] as JObject;
                if (bbox == null) return null;

                var minObj = bbox["min"] as JObject;
                var maxObj = bbox["max"] as JObject;
                if (minObj == null || maxObj == null) return null;

                XYZ min = new XYZ(
                    minObj["x"]?.Value<double>() ?? 0,
                    minObj["y"]?.Value<double>() ?? 0,
                    minObj["z"]?.Value<double>() ?? 0);

                XYZ max = new XYZ(
                    maxObj["x"]?.Value<double>() ?? 0,
                    maxObj["y"]?.Value<double>() ?? 0,
                    maxObj["z"]?.Value<double>() ?? 0);

                return new BoundingBoxXYZ { Min = min, Max = max };
            }
            catch
            {
                return null;
            }
        }
    }
}
