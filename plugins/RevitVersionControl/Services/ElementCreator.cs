using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;

namespace RevitVersionControl.Services
{
    public class CreationResult
    {
        public Element Element { get; set; }
        public bool IsUnsupported { get; set; }
        public bool IsSatisfiedByBundle { get; set; }
        public string Reason { get; set; }

        public static CreationResult Success(Element element) =>
            new CreationResult { Element = element };

        public static CreationResult Unsupported(string reason) =>
            new CreationResult { IsUnsupported = true, Reason = reason };

        public static CreationResult SatisfiedByBundle(string reason) =>
            new CreationResult { IsSatisfiedByBundle = true, Reason = reason };

        public static CreationResult Failed(string reason) =>
            new CreationResult { Reason = reason };
    }

    public class ElementCreator
    {
        private static readonly HashSet<string> _unsupportedCategories = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "Stairs",
            "Railings",
            "Railing",
            "Model Text",
            "Curtain Panels",
            "Curtain Wall Mullions",
            "Topography",
            "Model Groups",
            "Parts",
            "Assemblies",
            "Filled Region",
            "Detail Items",
        };

        private readonly Document _document;

        public ElementCreator(Document document)
        {
            _document = document;
        }

        public CreationResult Create(JObject newData)
        {
            if (newData == null)
                return CreationResult.Failed("newData is null");

            string category   = newData["category"]?.ToString()    ?? string.Empty;
            string familyName = newData["familyName"]?.ToString()  ?? string.Empty;
            string typeName   = newData["typeName"]?.ToString()    ?? string.Empty;
            var    location   = newData["location"]  as JObject;

            if (_unsupportedCategories.Contains(category))
                return CreationResult.Unsupported(
                    $"Category '{category}' cannot be created programmatically via the Revit API. " +
                    $"Please add this element manually (family: '{familyName}', type: '{typeName}').");

            try
            {
                return category switch
                {
                    "Walls"                                        => CreateWall(newData, location),
                    "Floors"                                       => CreateFloor(newData, location),
                    "Roofs"                                        => CreateRoof(newData, location),
                    "Structural Columns" or "Columns"              => CreateColumn(familyName, typeName, location),
                    "Structural Framing"                           => CreateBeam(familyName, typeName, location),
                    "Doors" or "Windows"                           => CreateHostedInstance(familyName, typeName, location, newData),
                    "Rooms"                                        => CreateRoom(location),
                    "Ducts"                                        => CreateDuct(typeName, location),
                    "Pipes"                                        => CreatePipe(typeName, location),
                    "Cable Trays"                                  => CreateCableTray(typeName, location),
                    "Conduits"                                     => CreateConduit(typeName, location),
                    _                                              => CreateGenericFamilyInstance(familyName, typeName, location),
                };
            }
            catch (Exception ex)
            {
                return CreationResult.Failed(
                    $"Exception creating '{category}' '{familyName}:{typeName}': {ex.Message}");
            }
        }

        private CreationResult CreateWall(JObject newData, JObject location)
        {
            if (location == null)
                return CreationResult.Failed("Wall: missing location data");

            var startPt = location["startPoint"] as JObject;
            var endPt   = location["endPoint"]   as JObject;
            if (startPt == null || endPt == null)
                return CreationResult.Failed("Wall: missing startPoint or endPoint");

            var start = ReadXYZ(startPt);
            var end   = ReadXYZ(endPt);

            if (start.DistanceTo(end) < 0.01)
                return CreationResult.Failed("Wall: start and end points are too close");

            string typeName = newData["typeName"]?.ToString() ?? string.Empty;
            WallType wallType = FindByName<WallType>(typeName);
            if (wallType == null)
                return CreationResult.Failed($"Wall: WallType '{typeName}' not found in document");

            Level level = GetNearestLevel(start.Z);
            if (level == null)
                return CreationResult.Failed("Wall: no levels found in document");

            var wall = Wall.Create(_document, Line.CreateBound(start, end),
                wallType.Id, level.Id, 10.0, 0, false, false);

            return CreationResult.Success(wall);
        }

        private CreationResult CreateFloor(JObject newData, JObject location)
        {
            var geometry = newData["geometry"] as JObject;
            var bbox     = geometry?["boundingBox"] as JObject;
            if (bbox == null)
                return CreationResult.Failed("Floor: missing bounding box geometry");

            var min = bbox["min"] as JObject;
            var max = bbox["max"] as JObject;
            if (min == null || max == null)
                return CreationResult.Failed("Floor: bounding box min/max missing");

            double x1 = min["x"]?.Value<double>() ?? 0;
            double y1 = min["y"]?.Value<double>() ?? 0;
            double z  = min["z"]?.Value<double>() ?? 0;
            double x2 = max["x"]?.Value<double>() ?? 0;
            double y2 = max["y"]?.Value<double>() ?? 0;

            string typeName = newData["typeName"]?.ToString() ?? string.Empty;
            FloorType floorType = FindByName<FloorType>(typeName);
            if (floorType == null)
                return CreationResult.Failed($"Floor: FloorType '{typeName}' not found in document");

            Level level = GetNearestLevel(z);
            if (level == null)
                return CreationResult.Failed("Floor: no levels found in document");

            var loop = new CurveLoop();
            loop.Append(Line.CreateBound(new XYZ(x1, y1, z), new XYZ(x2, y1, z)));
            loop.Append(Line.CreateBound(new XYZ(x2, y1, z), new XYZ(x2, y2, z)));
            loop.Append(Line.CreateBound(new XYZ(x2, y2, z), new XYZ(x1, y2, z)));
            loop.Append(Line.CreateBound(new XYZ(x1, y2, z), new XYZ(x1, y1, z)));

            var floor = Floor.Create(_document, new List<CurveLoop> { loop }, floorType.Id, level.Id);
            return CreationResult.Success(floor);
        }

        private CreationResult CreateRoof(JObject newData, JObject location)
        {
            var geometry = newData["geometry"] as JObject;
            var bbox     = geometry?["boundingBox"] as JObject;
            if (bbox == null)
                return CreationResult.Failed("Roof: missing bounding box geometry");

            var min = bbox["min"] as JObject;
            var max = bbox["max"] as JObject;
            if (min == null || max == null)
                return CreationResult.Failed("Roof: bounding box min/max missing");

            double x1 = min["x"]?.Value<double>() ?? 0;
            double y1 = min["y"]?.Value<double>() ?? 0;
            double z  = max["z"]?.Value<double>() ?? 0;
            double x2 = max["x"]?.Value<double>() ?? 0;
            double y2 = max["y"]?.Value<double>() ?? 0;

            string typeName = newData["typeName"]?.ToString() ?? string.Empty;
            RoofType roofType = FindByName<RoofType>(typeName);
            if (roofType == null)
                return CreationResult.Failed($"Roof: RoofType '{typeName}' not found in document");

            Level level = GetNearestLevel(z);
            if (level == null)
                return CreationResult.Failed("Roof: no levels found in document");

            var footprint = new CurveArray();
            footprint.Append(Line.CreateBound(new XYZ(x1, y1, z), new XYZ(x2, y1, z)));
            footprint.Append(Line.CreateBound(new XYZ(x2, y1, z), new XYZ(x2, y2, z)));
            footprint.Append(Line.CreateBound(new XYZ(x2, y2, z), new XYZ(x1, y2, z)));
            footprint.Append(Line.CreateBound(new XYZ(x1, y2, z), new XYZ(x1, y1, z)));

            ModelCurveArray mapping = new ModelCurveArray();
            var roof = _document.Create.NewFootPrintRoof(footprint, level, roofType, out mapping);
            return CreationResult.Success(roof);
        }

        private CreationResult CreateColumn(string familyName, string typeName, JObject location)
        {
            var symbol = FindFamilySymbol(familyName, typeName);
            if (symbol == null)
                return CreationResult.Failed(
                    $"Column: FamilySymbol '{familyName}:{typeName}' not found in document");

            ActivateSymbol(symbol);

            XYZ point = ReadLocationPoint(location);
            Level level = GetNearestLevel(point.Z);
            if (level == null)
                return CreationResult.Failed("Column: no levels found in document");

            var instance = _document.Create.NewFamilyInstance(
                point, symbol, level, StructuralType.Column);
            return CreationResult.Success(instance);
        }

        private CreationResult CreateBeam(string familyName, string typeName, JObject location)
        {
            if (location == null)
                return CreationResult.Failed("Beam: missing location data");

            var startPt = location["startPoint"] as JObject;
            var endPt   = location["endPoint"]   as JObject;
            if (startPt == null || endPt == null)
                return CreationResult.Failed("Beam: missing startPoint or endPoint");

            var symbol = FindFamilySymbol(familyName, typeName);
            if (symbol == null)
                return CreationResult.Failed(
                    $"Beam: FamilySymbol '{familyName}:{typeName}' not found in document");

            ActivateSymbol(symbol);

            var start = ReadXYZ(startPt);
            var end   = ReadXYZ(endPt);

            if (start.DistanceTo(end) < 0.01)
                return CreationResult.Failed("Beam: start and end points are too close");

            Level level = GetNearestLevel(start.Z);
            if (level == null)
                return CreationResult.Failed("Beam: no levels found in document");

            var instance = _document.Create.NewFamilyInstance(
                Line.CreateBound(start, end), symbol, level, StructuralType.Beam);
            return CreationResult.Success(instance);
        }

        private CreationResult CreateHostedInstance(
            string familyName, string typeName, JObject location, JObject newData)
        {
            var symbol = FindFamilySymbol(familyName, typeName);
            if (symbol == null)
                return CreationResult.Failed(
                    $"Hosted: FamilySymbol '{familyName}:{typeName}' not found in document");

            ActivateSymbol(symbol);

            XYZ point = ReadLocationPoint(location);

            string hostUniqueId = newData["hostId"]?.ToString();
            Element host = !string.IsNullOrEmpty(hostUniqueId)
                ? _document.GetElement(hostUniqueId)
                : null;

            if (host != null)
            {
                var instance = _document.Create.NewFamilyInstance(
                    point, symbol, host, StructuralType.NonStructural);
                return CreationResult.Success(instance);
            }

            Level level = GetNearestLevel(point.Z) ??
                new FilteredElementCollector(_document)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .FirstOrDefault();

            if (level == null)
                return CreationResult.Failed(
                    $"Hosted: host element '{hostUniqueId}' not found and no levels available");

            var fallback = _document.Create.NewFamilyInstance(
                point, symbol, level, StructuralType.NonStructural);

            return new CreationResult
            {
                Element  = fallback,
                Reason   = $"Warning: '{familyName}' placed without host - host '{hostUniqueId}' not found. " +
                            "Element may need to be re-hosted manually."
            };
        }

        private CreationResult CreateRoom(JObject location)
        {
            if (location == null)
                return CreationResult.Failed("Room: missing location data");

            var pointData = location["point"] as JObject;
            if (pointData == null)
                return CreationResult.Failed("Room: missing point in location");

            XYZ point = ReadXYZ(pointData);
            Level level = GetNearestLevel(point.Z);
            if (level == null)
                return CreationResult.Failed("Room: no levels found in document");

            var uvPoint = new UV(point.X, point.Y);
            var room = _document.Create.NewRoom(level, uvPoint);
            return CreationResult.Success(room);
        }

        private CreationResult CreateDuct(string typeName, JObject location)
        {
            var (start, end) = ReadCurveEndpoints(location);
            if (start == null)
                return CreationResult.Failed("Duct: missing or invalid location curve");

            MechanicalSystemType mst = new FilteredElementCollector(_document)
                .OfClass(typeof(MechanicalSystemType))
                .Cast<MechanicalSystemType>()
                .FirstOrDefault();
            if (mst == null)
                return CreationResult.Failed("Duct: no MechanicalSystemType found in document");

            DuctType ductType = FindByName<DuctType>(typeName)
                ?? new FilteredElementCollector(_document)
                    .OfClass(typeof(DuctType))
                    .Cast<DuctType>()
                    .FirstOrDefault();
            if (ductType == null)
                return CreationResult.Failed("Duct: no DuctType found in document");

            Level level = GetNearestLevel(start.Z);
            if (level == null)
                return CreationResult.Failed("Duct: no levels found in document");

            var duct = Duct.Create(_document, mst.Id, ductType.Id, level.Id, start, end);
            return CreationResult.Success(duct);
        }

        private CreationResult CreatePipe(string typeName, JObject location)
        {
            var (start, end) = ReadCurveEndpoints(location);
            if (start == null)
                return CreationResult.Failed("Pipe: missing or invalid location curve");

            PipingSystemType pst = new FilteredElementCollector(_document)
                .OfClass(typeof(PipingSystemType))
                .Cast<PipingSystemType>()
                .FirstOrDefault();
            if (pst == null)
                return CreationResult.Failed("Pipe: no PipingSystemType found in document");

            PipeType pipeType = FindByName<PipeType>(typeName)
                ?? new FilteredElementCollector(_document)
                    .OfClass(typeof(PipeType))
                    .Cast<PipeType>()
                    .FirstOrDefault();
            if (pipeType == null)
                return CreationResult.Failed("Pipe: no PipeType found in document");

            Level level = GetNearestLevel(start.Z);
            if (level == null)
                return CreationResult.Failed("Pipe: no levels found in document");

            var pipe = Pipe.Create(_document, pst.Id, pipeType.Id, level.Id, start, end);
            return CreationResult.Success(pipe);
        }

        private CreationResult CreateCableTray(string typeName, JObject location)
        {
            var (start, end) = ReadCurveEndpoints(location);
            if (start == null)
                return CreationResult.Failed("CableTray: missing or invalid location curve");

            CableTrayType trayType = FindByName<CableTrayType>(typeName)
                ?? new FilteredElementCollector(_document)
                    .OfClass(typeof(CableTrayType))
                    .Cast<CableTrayType>()
                    .FirstOrDefault();
            if (trayType == null)
                return CreationResult.Failed("CableTray: no CableTrayType found in document");

            Level level = GetNearestLevel(start.Z);
            if (level == null)
                return CreationResult.Failed("CableTray: no levels found in document");

            var tray = CableTray.Create(_document, trayType.Id, start, end, level.Id);
            return CreationResult.Success(tray);
        }

        private CreationResult CreateConduit(string typeName, JObject location)
        {
            var (start, end) = ReadCurveEndpoints(location);
            if (start == null)
                return CreationResult.Failed("Conduit: missing or invalid location curve");

            ConduitType conduitType = FindByName<ConduitType>(typeName)
                ?? new FilteredElementCollector(_document)
                    .OfClass(typeof(ConduitType))
                    .Cast<ConduitType>()
                    .FirstOrDefault();
            if (conduitType == null)
                return CreationResult.Failed("Conduit: no ConduitType found in document");

            Level level = GetNearestLevel(start.Z);
            if (level == null)
                return CreationResult.Failed("Conduit: no levels found in document");

            var conduit = Conduit.Create(_document, conduitType.Id, start, end, level.Id);
            return CreationResult.Success(conduit);
        }

        private CreationResult CreateGenericFamilyInstance(
            string familyName, string typeName, JObject location)
        {
            var symbol = FindFamilySymbol(familyName, typeName);
            if (symbol == null)
                return CreationResult.Failed(
                    $"FamilyInstance: symbol '{familyName}:{typeName}' not found in document. " +
                    "Ensure the family is loaded.");

            ActivateSymbol(symbol);

            XYZ point = ReadLocationPoint(location);
            Level level = GetNearestLevel(point.Z) ??
                new FilteredElementCollector(_document)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .FirstOrDefault();

            if (level == null)
                return CreationResult.Failed(
                    $"FamilyInstance: no levels found for '{familyName}:{typeName}'");

            var instance = _document.Create.NewFamilyInstance(
                point, symbol, level, StructuralType.NonStructural);
            return CreationResult.Success(instance);
        }

        public List<string> GetMissingFamilies(IEnumerable<Change> changes)
        {
            var loaded = new HashSet<string>(
                new FilteredElementCollector(_document)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .Select(fs => fs.FamilyName),
                StringComparer.OrdinalIgnoreCase);

            var missing = new List<string>();

            foreach (var change in changes ?? Enumerable.Empty<Change>())
            {
                if (change.ChangeType != "added" || change.NewData == null)
                    continue;

                var data       = JObject.FromObject(change.NewData);
                string family  = data["familyName"]?.ToString();
                string cat     = data["category"]?.ToString() ?? string.Empty;

                if (SyncCategoryRules.ShouldUsePayloadForAddedElement(cat))
                    continue;

                if (string.IsNullOrEmpty(family)
                    || cat == "Walls" || cat == "Floors" || cat == "Roofs"
                    || cat == "Rooms")
                    continue;

                if (!loaded.Contains(family) && !missing.Contains(family))
                    missing.Add(family);
            }

            return missing;
        }

        private T FindByName<T>(string name) where T : Element
        {
            if (string.IsNullOrEmpty(name))
                return new FilteredElementCollector(_document)
                    .OfClass(typeof(T))
                    .Cast<T>()
                    .FirstOrDefault();

            return new FilteredElementCollector(_document)
                       .OfClass(typeof(T))
                       .Cast<T>()
                       .FirstOrDefault(e => e.Name == name)
                   ?? new FilteredElementCollector(_document)
                       .OfClass(typeof(T))
                       .Cast<T>()
                       .FirstOrDefault();
        }

        private FamilySymbol FindFamilySymbol(string familyName, string typeName)
        {
            return new FilteredElementCollector(_document)
                       .OfClass(typeof(FamilySymbol))
                       .Cast<FamilySymbol>()
                       .FirstOrDefault(fs =>
                           fs.FamilyName == familyName && fs.Name == typeName)
                   ?? new FilteredElementCollector(_document)
                       .OfClass(typeof(FamilySymbol))
                       .Cast<FamilySymbol>()
                       .FirstOrDefault(fs => fs.FamilyName == familyName);
        }

        private static void ActivateSymbol(FamilySymbol symbol)
        {
            if (!symbol.IsActive)
                symbol.Activate();
        }

        private Level GetNearestLevel(double elevation)
        {
            return new FilteredElementCollector(_document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => Math.Abs(l.Elevation - elevation))
                .FirstOrDefault();
        }

        private static XYZ ReadXYZ(JObject obj)
        {
            return new XYZ(
                obj["x"]?.Value<double>() ?? 0,
                obj["y"]?.Value<double>() ?? 0,
                obj["z"]?.Value<double>() ?? 0);
        }

        private static XYZ ReadLocationPoint(JObject location)
        {
            if (location == null)
                return XYZ.Zero;

            var pt = location["point"] as JObject;
            return pt != null ? ReadXYZ(pt) : XYZ.Zero;
        }

        private static (XYZ start, XYZ end) ReadCurveEndpoints(JObject location)
        {
            if (location == null)
                return (null, null);

            var startPt = location["startPoint"] as JObject;
            var endPt   = location["endPoint"]   as JObject;

            if (startPt == null || endPt == null)
                return (null, null);

            var start = ReadXYZ(startPt);
            var end   = ReadXYZ(endPt);

            return start.DistanceTo(end) < 0.01 ? (null, null) : (start, end);
        }
    }
}
