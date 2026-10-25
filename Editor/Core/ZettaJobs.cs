using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace Zetta3D.Editor
{
    /// <summary>
    /// Follows every 3D job the plugin started until it finishes, then (optionally) imports it.
    /// The list survives domain reloads (SessionState), so writing a script while a model generates
    /// does not lose the job.
    /// </summary>
    [InitializeOnLoad]
    public static class ZettaJobs
    {
        [Serializable]
        public class Job
        {
            [JsonProperty("id")] public long Id;
            [JsonProperty("label")] public string Label;
            /// <summary>studio | agent</summary>
            [JsonProperty("source")] public string Source = "studio";
            [JsonProperty("agentSessionId")] public long AgentSessionId;
            [JsonProperty("autoImport")] public bool AutoImport = true;
            [JsonProperty("place")] public bool Place = true;
            [JsonProperty("parentPath")] public string ParentPath;
            [JsonProperty("position")] public float[] Position;
            [JsonProperty("folder")] public string Folder;
            [JsonProperty("status")] public string Status = "pending";
            [JsonProperty("queuePosition")] public int? QueuePosition;
            [JsonProperty("importedPath")] public string ImportedPath;
            [JsonProperty("error")] public string Error;
            [JsonProperty("startedAt")] public DateTime StartedAt = DateTime.UtcNow;
            [JsonIgnore] public Generation Latest { get; set; }
            [JsonIgnore] public bool Finished => Status is "success" or "failed" or "cancelled";
        }

        const string Key = "Zetta3D.Jobs";
        static List<Job> _jobs = new List<Job>();
        static CancellationTokenSource _cancel;

        public static IReadOnlyList<Job> All => _jobs;

        /// <summary>Any change to any job (status, queue position, import).</summary>
        public static event Action<Job> Updated;

        /// <summary>A job reached a terminal state (and, if requested, was imported).</summary>
        public static event Action<Job> Finished;

        static ZettaJobs()
        {
            try
            {
                _jobs = JsonConvert.DeserializeObject<List<Job>>(SessionState.GetString(Key, "[]")) ?? new List<Job>();
            }
            catch (Exception)
            {
                _jobs = new List<Job>();
            }
            AssemblyReloadEvents.beforeAssemblyReload += () => _cancel?.Cancel();
            EditorApplication.delayCall += Resume;
        }

        static void Save() => SessionState.SetString(Key, JsonConvert.SerializeObject(_jobs));

        public static Job Track(Generation generation, string label, Action<Job> configure = null)
        {
            var job = new Job { Id = generation.Id, Label = label, Status = generation.Status, Latest = generation };
            configure?.Invoke(job);
            _jobs.RemoveAll(j => j.Id == job.Id);
            _jobs.Insert(0, job);
            if (_jobs.Count > 50) _jobs.RemoveRange(50, _jobs.Count - 50);
            Save();
            Updated?.Invoke(job);
            Resume();
            return job;
        }

        public static void Forget(long id)
        {
            _jobs.RemoveAll(j => j.Id == id);
            Save();
            Updated?.Invoke(null);
        }

        public static void ClearFinished()
        {
            _jobs.RemoveAll(j => j.Finished);
            Save();
            Updated?.Invoke(null);
        }

        static void Resume()
        {
            if (_cancel != null && !_cancel.IsCancellationRequested) return;
            if (!_jobs.Any(j => !j.Finished)) return;
            _cancel = new CancellationTokenSource();
            _ = Poll(_cancel.Token);
        }

        static async Task Poll(CancellationToken cancel)
        {
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    var open = _jobs.Where(j => !j.Finished).ToList();
                    if (open.Count == 0) break;
                    if (!ZettaAuth.IsSignedIn)
                    {
                        await Task.Delay(3000, cancel);
                        continue;
                    }

                    foreach (var job in open)
                    {
                        try
                        {
                            var (generation, position) = await ZettaApi.GetGenerationAsync(job.Id, cancel);
                            job.Latest = generation;
                            var changed = job.Status != generation.Status || job.QueuePosition != position;
                            job.Status = generation.Status;
                            job.QueuePosition = position;
                            if (generation.Status == "failed") job.Error = generation.ErrorMessage ?? "The job failed. Your credits were refunded.";
                            if (job.Finished) await Complete(job, generation);
                            else if (changed)
                            {
                                Save();
                                Updated?.Invoke(job);
                            }
                        }
                        catch (ZettaApiException e) when (e.Status == 404)
                        {
                            job.Status = "failed";
                            job.Error = "This job is no longer on your account.";
                            Save();
                            Updated?.Invoke(job);
                            Finished?.Invoke(job);
                        }
                        catch (ZettaApiException)
                        {
                            // Transient (offline, 5xx): try again on the next round.
                        }
                    }

                    await Task.Delay(2500, cancel);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _cancel = null;
            }
        }

        static async Task Complete(Job job, Generation generation)
        {
            // A splat (only from a job started by an older version) is not supported in Unity: nothing to import.
            if (generation.IsSuccess && job.AutoImport && !generation.IsSplat)
            {
                try
                {
                    var place = job.Place
                        ? new ZettaImporter.Placement
                        {
                            Parent = string.IsNullOrEmpty(job.ParentPath) ? null : ZettaScene.FindByPath(job.ParentPath)?.transform,
                            Position = job.Position is { Length: 3 } p ? new Vector3(p[0], p[1], p[2]) : (Vector3?)null,
                            Select = job.Source == "studio",
                        }
                        : null;
                    var result = await ZettaImporter.ImportAsync(generation, job.Folder, place);
                    job.ImportedPath = result.MainAssetPath;
                }
                catch (Exception e)
                {
                    job.Error = "Finished, but the import failed: " + e.Message;
                    Debug.LogWarning($"[Zetta 3D] {job.Error}");
                }
            }
            Save();
            Updated?.Invoke(job);
            Finished?.Invoke(job);
        }
    }
}
