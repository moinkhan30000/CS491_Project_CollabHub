using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace RevitVersionControl.Services
{
    /// <summary>
    /// API client for communicating with backend server
    /// </summary>
    public class ApiClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private string _authToken;

        public ApiClient(string baseUrl = "http://localhost:8000/api/v1")
        {
            _baseUrl = baseUrl;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        public void SetAuthToken(string token)
        {
            _authToken = token;
            _httpClient.DefaultRequestHeaders.Remove("Authorization");
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        }

        // ========== Authentication ==========

        public async Task<LoginResponse> LoginAsync(string email, string password)
        {
            var payload = new { email, password };
            var response = await PostAsync<LoginResponse>("/auth/login", payload);
            
            if (response != null)
            {
                SetAuthToken(response.AccessToken);
            }
            
            return response;
        }

        // ========== Projects ==========

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

        // ========== Snapshots & Commits ==========

        public async Task<Commit> PublishSnapshotAsync(string projectId, ElementSnapshot snapshot)
        {
            var payload = new
            {
                modelId = snapshot.ModelId,
                commitMessage = snapshot.CommitMessage,
                parentCommit = snapshot.ParentCommit,
                snapshot = snapshot
            };
            
            return await PostAsync<Commit>($"/projects/{projectId}/snapshots", payload);
        }

        public async Task<List<Commit>> GetCommitsAsync(string projectId, int limit = 50, int offset = 0)
        {
            var response = await GetAsync<CommitsResponse>($"/projects/{projectId}/commits?limit={limit}&offset={offset}");
            return response?.Commits ?? new List<Commit>();
        }

        public async Task<ElementSnapshot> GetSnapshotAsync(string projectId, string commitId)
        {
            return await GetAsync<ElementSnapshot>($"/projects/{projectId}/commits/{commitId}/snapshot");
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

        // ========== Generic HTTP Methods ==========

        private async Task<T> GetAsync<T>(string endpoint)
        {
            try
            {
                var response = await _httpClient.GetAsync(_baseUrl + endpoint);
                response.EnsureSuccessStatusCode();
                
                var content = await response.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<T>(content);
            }
            catch (Exception ex)
            {
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
                response.EnsureSuccessStatusCode();
                
                var responseContent = await response.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<T>(responseContent);
            }
            catch (Exception ex)
            {
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
    }

    public class CommitsResponse
    {
        [JsonProperty("commits")]
        public List<Commit> Commits { get; set; }
        
        [JsonProperty("total")]
        public int Total { get; set; }
    }

    public class Commit
    {
        [JsonProperty("commitId")]
        public string CommitId { get; set; }
        
        [JsonProperty("message")]
        public string Message { get; set; }
        
        [JsonProperty("timestamp")]
        public DateTime Timestamp { get; set; }
        
        [JsonProperty("author")]
        public object Author { get; set; }
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
        
        [JsonProperty("category")]
        public string Category { get; set; }
        
        [JsonProperty("type")]
        public string Type { get; set; }
        
        [JsonProperty("parameterChanges")]
        public List<object> ParameterChanges { get; set; }
    }

    public class PullResult
    {
        [JsonProperty("changes")]
        public List<Change> Changes { get; set; }
        
        [JsonProperty("conflicts")]
        public List<object> Conflicts { get; set; }
        
        [JsonProperty("requiresResolution")]
        public bool RequiresResolution { get; set; }
    }
}
