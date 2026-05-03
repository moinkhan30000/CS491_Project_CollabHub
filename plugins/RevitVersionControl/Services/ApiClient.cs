using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.IO;

namespace RevitVersionControl.Services
{
    public class ApiClient
    {
        private static ApiClient _instance;
        public static ApiClient Instance => _instance ?? (_instance = new ApiClient());

        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private string _authToken;
        public string LastError { get; private set; }
        public string CurrentUserEmail { get; private set; }

        public bool IsLoggedIn => !string.IsNullOrEmpty(_authToken);

        private ApiClient(string baseUrl = "http://localhost:8000/api/v1")
        {
            _baseUrl = baseUrl;
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(120);
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        public void SetAuthToken(string token)
        {
            _authToken = token;
            _httpClient.DefaultRequestHeaders.Remove("Authorization");
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        }

        public async Task<bool> LoginAsync(string email, string password)
        {
            var payload = new { email, password };
            var response = await PostAsync<LoginResponse>("/auth/login", payload);
            
            if (response != null && !string.IsNullOrEmpty(response.AccessToken))
            {
                SetAuthToken(response.AccessToken);
                CurrentUserEmail = email?.Trim();
                return true;
            }
            
            return false;
        }

        public async Task<bool> RegisterAsync(string fullName, string email, string password)
        {
            var payload = new 
            { 
                fullName,
                email, 
                password 
            };
            
            var response = await PostAsync<LoginResponse>("/auth/register", payload);
            
            if (response != null && !string.IsNullOrEmpty(response.AccessToken))
            {
                SetAuthToken(response.AccessToken);
                CurrentUserEmail = email?.Trim();
                return true;
            }
            
            return false;
        }

        public void Logout()
        {
            _authToken = null;
            CurrentUserEmail = null;
            _httpClient.DefaultRequestHeaders.Remove("Authorization");
        }

        public async Task<List<Project>> GetProjectsAsync()
        {
            var response = await GetAsync<ProjectsResponse>("/projects");
            return response?.Projects ?? new List<Project>();
        }

        public async Task<Project> CreateProjectAsync(string name, string description)
        {
            var payload = new { name, description };
            return await PostAsync<Project>("/projects", payload);
        }

        public async Task<Project> InitProjectAsync(string name, string filePath, ElementSnapshot initialSnapshot = null)
        {
            using (var content = new MultipartFormDataContent())
            {
                content.Add(new StringContent(name), "name");
                content.Add(new StringContent(filePath), "modelId");
                if (initialSnapshot != null)
                {
                    string snapshotJson = JsonConvert.SerializeObject(initialSnapshot);
                    content.Add(new StringContent(snapshotJson, Encoding.UTF8, "application/json"), "snapshotJson");
                }

                using (var fileStream = new System.IO.FileStream(filePath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                {
                    var memoryStream = new System.IO.MemoryStream();
                    await fileStream.CopyToAsync(memoryStream).ConfigureAwait(false);
                    memoryStream.Position = 0;

                    var fileContent = new ByteArrayContent(memoryStream.ToArray());
                    fileContent.Headers.Add("Content-Type", "application/octet-stream");
                    content.Add(fileContent, "file", System.IO.Path.GetFileName(filePath));

                    var response = await _httpClient.PostAsync(_baseUrl + "/projects/init", content).ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        throw new HttpRequestException($"Server Error {response.StatusCode}: {errorContent}");
                    }

                    var responseContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return JsonConvert.DeserializeObject<Project>(responseContent);
                }
            }
        }

        public async Task<bool> InviteUserAsync(string projectId, string email)
        {
            try
            {
                var response = await _httpClient.PostAsync($"{_baseUrl}/projects/{projectId}/invite?email={Uri.EscapeDataString(email)}", null);
                if (response.IsSuccessStatusCode) return true;
                return false;
            }
            catch { return false; }
        }

        public async Task<List<Invite>> GetPendingInvitesAsync()
        {
            var response = await GetAsync<List<Invite>>("/projects/invitations/pending");
            return response ?? new List<Invite>();
        }

        public async Task<string> RespondToInviteAsync(int inviteId, string status, string savePath = null)
        {
            try
            {
                var response = await _httpClient.PostAsync($"{_baseUrl}/projects/invitations/{inviteId}/respond?status={status}", null);
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    return $"Error: HTTP {(int)response.StatusCode}: {errorContent}";
                }

                if (status == "ACTIVE" && !string.IsNullOrEmpty(savePath))
                {
                    var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
                    if (!mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();
                        return $"Error: Unexpected response: {errorContent}";
                    }

                    using (var stream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new System.IO.FileStream(savePath, System.IO.FileMode.Create))
                    {
                        await stream.CopyToAsync(fileStream);
                    }
                    return savePath;
                }
                
                return "Success";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        // ========== Branches ==========

        public async Task<List<Branch>> GetBranchesAsync(string projectId)
        {
            var response = await GetAsync<List<Branch>>($"/projects/{projectId}/branches");
            return response ?? new List<Branch>();
        }

        public async Task<Branch> CreateBranchAsync(string projectId, string branchName, string headCommitId = null)
        {
            var payload = new { name = branchName, headCommitId = headCommitId };
            return await PostAsync<Branch>($"/projects/{projectId}/branches", payload);
        }

        public async Task<bool> DeleteBranchAsync(string projectId, string branchName)
        {
            try
            {
                var response = await _httpClient.DeleteAsync($"{_baseUrl}/projects/{projectId}/branches/{Uri.EscapeDataString(branchName)}");
                if (response.IsSuccessStatusCode) return true;
                
                var errorContent = await response.Content.ReadAsStringAsync();
                LastError = $"HTTP {(int)response.StatusCode}: {errorContent}";
                return false;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        // ========== Snapshots & Commits ==========

        public async Task<Commit> PublishSnapshotAsync(string projectId, ElementSnapshot snapshot, string branchName = null)
        {
            var payload = new
            {
                modelId = snapshot.ModelId,
                commitMessage = snapshot.CommitMessage,
                parentCommit = snapshot.ParentCommit,
                branchName = branchName,
                snapshot = snapshot
            };
            
            return await PostAsync<Commit>($"/projects/{projectId}/snapshots", payload);
        }

        public async Task<Commit> PublishPackageAsync(string projectId, CommitPackage package)
        {
            return await PostAsync<Commit>($"/projects/{projectId}/packages", package);
        }

        public async Task<List<Commit>> GetCommitsAsync(string projectId, int limit = 50, int offset = 0)
        {
            var response = await GetAsync<CommitsResponse>($"/projects/{projectId}/commits?limit={limit}&offset={offset}");
            return response?.Commits ?? new List<Commit>();
        }

        public async Task<Commit> GetLatestCommitAsync(string projectId)
        {
            var commits = await GetCommitsAsync(projectId, limit: 1, offset: 0);
            if (commits == null || commits.Count == 0)
                return null;

            return commits[0];
        }

        public async Task<Commit> GetProjectRootCommitAsync(string projectId)
        {
            return await GetAsync<Commit>($"/projects/{projectId}/commits/root");
        }

        public async Task<Commit> GetBaseModelCommitAsync(string projectId)
        {
            return await GetProjectRootCommitAsync(projectId);
        }

        public async Task<ElementSnapshot> GetSnapshotAsync(string projectId, string commitId)
        {
            return await GetAsync<ElementSnapshot>($"/projects/{projectId}/commits/{commitId}/snapshot");
        }

        // ========== Base File Operations ==========

        public async Task<BaseFileStatus> GetBaseFileStatusAsync(string projectId, string modelId)
        {
            string encodedModelId = Uri.EscapeDataString(modelId ?? string.Empty);
            return await GetAsync<BaseFileStatus>($"/projects/{projectId}/base-file/status?modelId={encodedModelId}");
        }

        public async Task<PayloadStatus> GetPayloadStatusAsync(string projectId, string contentHash)
        {
            string encodedHash = Uri.EscapeDataString(contentHash ?? string.Empty);
            return await GetAsync<PayloadStatus>($"/projects/{projectId}/payloads/status?contentHash={encodedHash}");
        }

        public async Task<PayloadRef> UploadPayloadAsync(
            string projectId,
            string contentHash,
            string filePath,
            IEnumerable<string> categories,
            IEnumerable<string> markers)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    LastError = "Payload file path is invalid or missing.";
                    return null;
                }

                using (var form = new MultipartFormDataContent())
                using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var fileContent = new StreamContent(fileStream))
                {
                    fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    form.Add(new StringContent(contentHash ?? string.Empty), "contentHash");
                    form.Add(new StringContent(JsonConvert.SerializeObject(categories ?? Array.Empty<string>())), "categoriesJson");
                    form.Add(new StringContent(JsonConvert.SerializeObject(markers ?? Array.Empty<string>())), "markersJson");
                    form.Add(fileContent, "file", Path.GetFileName(filePath));

                    var response = await _httpClient.PostAsync($"{_baseUrl}/projects/{projectId}/payloads", form);
                    var responseContent = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        LastError = $"HTTP {(int)response.StatusCode}: {responseContent}";
                        return null;
                    }

                    LastError = null;
                    return JsonConvert.DeserializeObject<PayloadRef>(responseContent);
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Console.WriteLine($"Payload upload failed: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> DownloadPayloadAsync(string projectId, string payloadId, string savePath)
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/projects/{projectId}/payloads/{Uri.EscapeDataString(payloadId ?? string.Empty)}");
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    LastError = $"HTTP {(int)response.StatusCode}: {errorContent}";
                    return false;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(savePath) ?? string.Empty);
                using (var stream = await response.Content.ReadAsStreamAsync())
                using (var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await stream.CopyToAsync(fileStream);
                }

                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Console.WriteLine($"Payload download failed: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> UploadBaseFileAsync(string projectId, string modelId, string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    LastError = "Base file path is invalid or missing.";
                    return false;
                }

                string encodedModelId = Uri.EscapeDataString(modelId ?? string.Empty);
                string endpoint = $"/projects/{projectId}/base-file?modelId={encodedModelId}";

                using (var form = new MultipartFormDataContent())
                using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var fileContent = new StreamContent(fileStream))
                {
                    fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    form.Add(fileContent, "file", Path.GetFileName(filePath));

                    var response = await _httpClient.PostAsync(_baseUrl + endpoint, form);
                    var responseContent = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        LastError = $"HTTP {(int)response.StatusCode}: {responseContent}";
                        return false;
                    }
                }

                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Console.WriteLine($"Base file upload failed: {ex.Message}");
                return false;
            }
        }

        // ========== Diff Operations ==========

        public async Task<DiffResult> GetDiffAsync(string projectId, string baseCommit, string targetCommit)
        {
            return await GetAsync<DiffResult>($"/projects/{projectId}/diff?base={baseCommit}&target={targetCommit}");
        }

        // ========== Merge Operations ==========

        public async Task<PullResult> PullChangesAsync(string projectId, string currentCommit, string targetCommit)
        {
            var payload = new
            {
                currentCommit,
                targetCommit,
                strategy = "auto"
            };
            
            return await PostAsync<PullResult>($"/projects/{projectId}/pull", payload);
        }

        public async Task<Merge3WayResult> Merge3WayAsync(string projectId, string sourceCommitId, string targetCommitId)
        {
            var payload = new
            {
                sourceCommitId,
                targetCommitId
            };
            return await PostAsync<Merge3WayResult>($"/projects/{projectId}/merge3way", payload);
        }

        // ========== Generic HTTP Methods ==========

        private async Task<T> GetAsync<T>(string endpoint)
        {
            try
            {
                var response = await _httpClient.GetAsync(_baseUrl + endpoint);
                var content = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    LastError = $"HTTP {(int)response.StatusCode}: {content}";
                    return default(T);
                }

                LastError = null;
                return JsonConvert.DeserializeObject<T>(content);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Console.WriteLine($"GET request failed: {ex.Message}");
                return default(T);
            }
        }

        private async Task<T> PostAsync<T>(string endpoint, object payload)
        {
            try
            {
                var json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                
                var response = await _httpClient.PostAsync(_baseUrl + endpoint, content);
                var responseContent = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    LastError = $"HTTP {(int)response.StatusCode}: {responseContent}";
                    return default(T);
                }

                LastError = null;
                return JsonConvert.DeserializeObject<T>(responseContent);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Console.WriteLine($"POST request failed: {ex.Message}");
                return default(T);
            }
        }
    }

    // ========== Response Models ==========

    public class LoginResponse
    {
        [JsonProperty("accessToken")]
        public string AccessToken { get; set; }
        
        [JsonProperty("refreshToken")]
        public string RefreshToken { get; set; }
        
        [JsonProperty("expiresIn")]
        public int ExpiresIn { get; set; }
    }

    public class ProjectsResponse
    {
        [JsonProperty("projects")]
        public List<Project> Projects { get; set; }
    }

    public class Project
    {
        [JsonProperty("projectId")]
        public string ProjectId { get; set; }
        
        [JsonProperty("name")]
        public string Name { get; set; }
        
        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("modelId")]
        public string ModelId { get; set; }

        [JsonProperty("baseCommitId")]
        public string BaseCommitId { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }
    }

    public class CommitsResponse
    {
        [JsonProperty("commits")]
        public List<Commit> Commits { get; set; }
        
        [JsonProperty("total")]
        public int Total { get; set; }
    }

    public class Branch
    {
        [JsonProperty("branchId")]
        public string BranchId { get; set; }

        [JsonProperty("projectId")]
        public string ProjectId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("headCommitId")]
        public string HeadCommitId { get; set; }

        [JsonProperty("createdAt")]
        public DateTime CreatedAt { get; set; }

        [JsonProperty("createdBy")]
        public string CreatedBy { get; set; }

        [JsonProperty("creatorName")]
        public string CreatorName { get; set; }
    }

    public class Commit
    {
        [JsonProperty("commitId")]
        public string CommitId { get; set; }

        [JsonProperty("projectId")]
        public string ProjectId { get; set; }

        [JsonProperty("modelId")]
        public string ModelId { get; set; }
        
        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("parentCommit")]
        public string ParentCommit { get; set; }

        [JsonProperty("parentCommit2")]
        public string ParentCommit2 { get; set; }

        [JsonProperty("changedElements")]
        public int ChangedElements { get; set; }

        [JsonProperty("elementCount")]
        public int ElementCount { get; set; }
        
        [JsonProperty("timestamp")]
        public DateTime Timestamp { get; set; }
        
        [JsonProperty("branchName")]
        public string BranchName { get; set; }
        
        [JsonProperty("author")]
        public object Author { get; set; }

        public string GetAuthorName()
        {
            if (Author == null)
            {
                return "Unknown";
            }

            if (Author is string authorString)
            {
                return authorString;
            }

            try
            {
                return ((Newtonsoft.Json.Linq.JObject)Author)["fullName"]?.ToString() ?? "Unknown";
            }
            catch
            {
                return "Unknown";
            }
        }
    }

    public class ElementSnapshot
    {
        [JsonProperty("version")]
        public string Version { get; set; } = "1.0";
        
        [JsonProperty("projectId")]
        public string ProjectId { get; set; }
        
        [JsonProperty("modelId")]
        public string ModelId { get; set; }
        
        [JsonProperty("timestamp")]
        public DateTime Timestamp { get; set; }
        
        [JsonProperty("userName")]
        public string UserName { get; set; }
        
        [JsonProperty("commitMessage")]
        public string CommitMessage { get; set; }
        
        [JsonProperty("elements")]
        public List<object> Elements { get; set; }
        
        [JsonProperty("parentCommit")]
        public string ParentCommit { get; set; }

        [JsonProperty("parentCommit2")]
        public string ParentCommit2 { get; set; }
    }

    public class CommitPackage
    {
        [JsonProperty("modelId")]
        public string ModelId { get; set; }

        [JsonProperty("commitMessage")]
        public string CommitMessage { get; set; }

        [JsonProperty("parentCommit")]
        public string ParentCommit { get; set; }

        [JsonProperty("parentCommit2")]
        public string ParentCommit2 { get; set; }

        [JsonProperty("changes")]
        public List<Change> Changes { get; set; }

        [JsonProperty("elementCount")]
        public int ElementCount { get; set; }

        [JsonProperty("payloadRefs")]
        public List<PayloadRef> PayloadRefs { get; set; }

        [JsonProperty("checkpointSnapshot")]
        public ElementSnapshot CheckpointSnapshot { get; set; }

        [JsonProperty("branchName")]
        public string BranchName { get; set; }
    }

    public class BaseFileStatus
    {
        [JsonProperty("exists")]
        public bool Exists { get; set; }

        [JsonProperty("fileName")]
        public string FileName { get; set; }
    }

    public class DiffResult
    {
        [JsonProperty("baseVersion")]
        public string BaseVersion { get; set; }
        
        [JsonProperty("targetVersion")]
        public string TargetVersion { get; set; }
        
        [JsonProperty("summary")]
        public Dictionary<string, int> Summary { get; set; }
        
        [JsonProperty("changes")]
        public List<Change> Changes { get; set; }
        
        [JsonProperty("conflicts")]
        public List<object> Conflicts { get; set; }
    }

    public class Change
    {
        [JsonProperty("changeType")]
        public string ChangeType { get; set; }
        
        [JsonProperty("elementId")]
        public string ElementId { get; set; }

        [JsonProperty("repoGuid")]
        public string RepoGuid { get; set; }
        
        [JsonProperty("category")]
        public string Category { get; set; }
        
        [JsonProperty("type")]
        public string Type { get; set; }
        
        [JsonProperty("parameterChanges")]
        public List<ParameterChange> ParameterChanges { get; set; }

        [JsonProperty("geometryChanged")]
        public bool GeometryChanged { get; set; }

        [JsonProperty("locationChanged")]
        public bool LocationChanged { get; set; }

        [JsonProperty("oldData")]
        public Dictionary<string, object> OldData { get; set; }

        [JsonProperty("newData")]
        public Dictionary<string, object> NewData { get; set; }
    }

    public class ParameterChange
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("oldValue")]
        public object OldValue { get; set; }

        [JsonProperty("newValue")]
        public object NewValue { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("elementName")]
        public string ElementName { get; set; }
    }

    public class PayloadRef
    {
        [JsonProperty("payloadId")]
        public string PayloadId { get; set; }

        [JsonProperty("storageUrl")]
        public string StorageUrl { get; set; }

        [JsonProperty("contentHash")]
        public string ContentHash { get; set; }

        [JsonProperty("categories")]
        public List<string> Categories { get; set; }

        [JsonProperty("markers")]
        public List<string> Markers { get; set; }
    }

    public class PayloadStatus
    {
        [JsonProperty("exists")]
        public bool Exists { get; set; }

        [JsonProperty("payload")]
        public PayloadRef Payload { get; set; }
    }

    public class Conflict
    {
        [JsonProperty("elementId")]
        public string ElementId { get; set; }

        [JsonProperty("conflictType")]
        public string ConflictType { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("localChange")]
        public Dictionary<string, object> LocalChange { get; set; }

        [JsonProperty("remoteChange")]
        public Dictionary<string, object> RemoteChange { get; set; }

        [JsonProperty("resolutionOptions")]
        public List<string> ResolutionOptions { get; set; }
    }

    public class PullResult
    {
        [JsonProperty("changes")]
        public List<Change> Changes { get; set; }
        
        [JsonProperty("conflicts")]
        public List<Conflict> Conflicts { get; set; }
        
        [JsonProperty("requiresResolution")]
        public bool RequiresResolution { get; set; }
    }

    public class Merge3WayResult
    {
        [JsonProperty("commonAncestorId")]
        public string CommonAncestorId { get; set; }

        [JsonProperty("sourceChanges")]
        public List<Change> SourceChanges { get; set; }

        [JsonProperty("targetChanges")]
        public List<Change> TargetChanges { get; set; }

        [JsonProperty("conflicts")]
        public List<Conflict> Conflicts { get; set; }

        [JsonProperty("hasConflicts")]
        public bool HasConflicts { get; set; }
    }

    public class Invite
    {
        [JsonProperty("inviteId")]
        public int InviteId { get; set; }
        [JsonProperty("projectId")]
        public string ProjectId { get; set; }
        [JsonProperty("projectName")]
        public string ProjectName { get; set; }
        [JsonProperty("invitedAt")]
        public DateTime InvitedAt { get; set; }
        [JsonProperty("role")]
        public string Role { get; set; }
        [JsonProperty("fileExtension")]
        public string FileExtension { get; set; } = ".rvt";
    }
}
