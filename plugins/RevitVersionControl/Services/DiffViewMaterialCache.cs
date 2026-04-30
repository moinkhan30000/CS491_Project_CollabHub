using System;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitVersionControl.Services
{
    public enum DiffChangeKind
    {
        Added,
        Modified,
        Deleted
    }

    internal static class DiffViewColors
    {
        public static readonly Color Added = new Color(0, 200, 83);     // #00C853
        public static readonly Color Modified = new Color(255, 214, 0);  // #FFD600
        public static readonly Color Deleted = new Color(213, 0, 0);    // #D50000
    }

    public class DiffViewMaterialCache
    {
        private const string AddedMaterialName = "RVCS_Diff_Added";
        private const string ModifiedMaterialName = "RVCS_Diff_Modified";
        private const string DeletedMaterialName = "RVCS_Diff_Deleted";

        public static ElementId GetMaterialId(Document doc, DiffChangeKind kind)
        {
            switch (kind)
            {
                case DiffChangeKind.Added:
                    return GetOrCreateMaterial(doc, AddedMaterialName, DiffViewColors.Added, 50);
                case DiffChangeKind.Modified:
                    return GetOrCreateMaterial(doc, ModifiedMaterialName, DiffViewColors.Modified, 50);
                case DiffChangeKind.Deleted:
                    return GetOrCreateMaterial(doc, DeletedMaterialName, DiffViewColors.Deleted, 50);
                default:
                    return ElementId.InvalidElementId;
            }
        }

        public static ElementId GetSolidFillPatternId(Document doc)
        {
            try
            {
                var solid = new FilteredElementCollector(doc)
                    .OfClass(typeof(FillPatternElement))
                    .Cast<FillPatternElement>()
                    .FirstOrDefault(fp =>
                    {
                        var fpat = fp.GetFillPattern();
                        return fpat != null
                            && fpat.IsSolidFill
                            && fpat.Target == FillPatternTarget.Drafting;
                    });
                return solid?.Id ?? ElementId.InvalidElementId;
            }
            catch
            {
                return ElementId.InvalidElementId;
            }
        }

        private static ElementId GetOrCreateMaterial(Document doc, string name, Color color, int transparency)
        {
            try
            {
                Material existing = new FilteredElementCollector(doc)
                    .OfClass(typeof(Material))
                    .Cast<Material>()
                    .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                    return existing.Id;

                ElementId newId = Material.Create(doc, name);
                Material material = doc.GetElement(newId) as Material;
                if (material != null)
                {
                    material.Color = color;
                    material.SurfaceForegroundPatternColor = color;
                    material.Transparency = transparency;
                    material.UseRenderAppearanceForShading = false;
                }
                return newId;
            }
            catch
            {
                return ElementId.InvalidElementId;
            }
        }
    }
}
