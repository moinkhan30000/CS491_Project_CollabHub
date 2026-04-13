using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Newtonsoft.Json.Linq;

namespace RevitVersionControl.Services
{
    internal static class RepoGuidService
    {
        private static readonly Guid SchemaGuid = new Guid("8f9f5f55-8c2a-4cf1-95ab-8d2b31c7f001");
        private const string SchemaName = "RevitVersionControl.RepoIdentity";
        private const string RepoGuidFieldName = "RepoGuid";

        public static string GetRepoGuid(Element element)
        {
            if (element == null)
                return null;

            try
            {
                Schema schema = GetOrCreateSchema();
                Entity entity = element.GetEntity(schema);
                if (entity == null || !entity.IsValid())
                    return null;

                Field field = schema.GetField(RepoGuidFieldName);
                if (field == null)
                    return null;

                string repoGuid = entity.Get<string>(field);
                return string.IsNullOrWhiteSpace(repoGuid) ? null : repoGuid;
            }
            catch
            {
                return null;
            }
        }

        public static bool SetRepoGuid(Element element, string repoGuid)
        {
            if (element == null || string.IsNullOrWhiteSpace(repoGuid))
                return false;

            try
            {
                Schema schema = GetOrCreateSchema();
                Field field = schema.GetField(RepoGuidFieldName);
                if (field == null)
                    return false;

                Entity entity = element.GetEntity(schema);
                if (entity == null || !entity.IsValid())
                    entity = new Entity(schema);

                entity.Set(field, repoGuid);
                element.SetEntity(entity);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static int EnsureRepoGuids(Document document, IDictionary<string, string> knownRepoGuids = null)
        {
            if (document == null)
                return 0;

            int assigned = 0;
            var collector = new FilteredElementCollector(document)
                .WhereElementIsNotElementType()
                .WhereElementIsViewIndependent();

            foreach (Element element in collector)
            {
                if (element == null || ShouldSkipIdentityAssignment(element))
                    continue;

                if (!string.IsNullOrWhiteSpace(GetRepoGuid(element)))
                    continue;

                string repoGuid = null;
                if (knownRepoGuids != null && !string.IsNullOrWhiteSpace(element.UniqueId))
                    knownRepoGuids.TryGetValue(element.UniqueId, out repoGuid);

                repoGuid ??= Guid.NewGuid().ToString("D");
                if (SetRepoGuid(element, repoGuid))
                    assigned++;
            }

            return assigned;
        }

        public static Dictionary<string, string> BuildKnownRepoGuidMap(ElementSnapshot snapshot)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (snapshot?.Elements == null)
                return result;

            foreach (object rawElement in snapshot.Elements)
            {
                try
                {
                    JObject element = rawElement as JObject ?? JObject.FromObject(rawElement);
                    string uniqueId = element["id"]?.ToString();
                    string repoGuid = element["repoGuid"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(uniqueId) && !string.IsNullOrWhiteSpace(repoGuid))
                        result[uniqueId] = repoGuid;
                }
                catch
                {
                    // Ignore malformed cache elements.
                }
            }

            return result;
        }

        public static Element FindElement(Document document, string repoGuid, string uniqueId)
        {
            if (document == null)
                return null;

            if (!string.IsNullOrWhiteSpace(repoGuid))
            {
                try
                {
                    Element byRepoGuid = new FilteredElementCollector(document)
                        .WhereElementIsNotElementType()
                        .FirstOrDefault(e => string.Equals(GetRepoGuid(e), repoGuid, StringComparison.OrdinalIgnoreCase));

                    if (byRepoGuid != null)
                        return byRepoGuid;
                }
                catch
                {
                    // Fall through to uniqueId lookup.
                }
            }

            if (!string.IsNullOrWhiteSpace(uniqueId))
            {
                try
                {
                    return document.GetElement(uniqueId);
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        private static Schema GetOrCreateSchema()
        {
            Schema schema = Schema.Lookup(SchemaGuid);
            if (schema != null)
                return schema;

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName(SchemaName);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(RepoGuidFieldName, typeof(string));
            return builder.Finish();
        }

        private static bool ShouldSkipIdentityAssignment(Element element)
        {
            if (element is View || element is ViewSheet || element is ScheduleSheetInstance)
                return true;
            if (element.Category?.Name?.Contains("Sketch") == true)
                return true;

            string category = element.Category?.Name ?? string.Empty;
            return SyncCategoryRules.IsAutoGeneratedOrInternalCategory(category);
        }
    }
}
