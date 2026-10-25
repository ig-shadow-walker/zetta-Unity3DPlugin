using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace Zetta3D.Editor.Pipelines
{
    /// <summary>Which finished mesh steps <see cref="RunFollower"/> imports into the project.</summary>
    public enum RunImportMode
    {
        /// <summary>Nothing is imported.</summary>
        None,
        /// <summary>The mesh steps nothing in the run's graph takes further — the run's results.</summary>
        FinalSteps,
        /// <summary>Every mesh step (a generation, then its retopology, its texture, …).</summary>
        AllMeshSteps,
    }

    /// <summary>
    /// KEEPS AN EYE ON RUNS (use-pipelines.ts polling, plus the Studio's own job follower): every 3 s while a followed
    /// run is running, paused or waiting for the user, and the runs list of a followed pipeline while any of its runs is.
    /// Survives domain reloads through SessionState. Optionally imports each finished mesh step through
    /// <see cref="ZettaImporter.ImportAsync"/> (never a splat). Events arrive on the main thread.
    /// </summary>
    [InitializeOnLoad]
    public static class RunFollower
    {
        public const int PollMs = 3000;
        /// <summary>A draft (not started) is checked this often instead — it changes only when someone presses Start.</summary>
        public const int DraftPollMs = 15000;
        const string Key = "Zetta3D.Pipelines.Followed";

        /// <summary>One followed run, as kept across reloads.</summary>
        public sealed class Followed
        {
            [JsonProperty("runId")] public long RunId;
            [JsonProperty("pipelineId")] public long PipelineId;
            /// <summary>The agent session that started it (0 = the user), so the agent runtime can send pipeline_run_finished.</summary>
            [JsonProperty("agentSessionId")] public long AgentSessionId;
            [JsonProperty("import")] public RunImportMode Import;
            /// <summary>Where imports go; null = the project's models folder.</summary>
            [JsonProperty("folder")] public string Folder;
            [JsonProperty("importedSteps")] public List<long> ImportedSteps = new List<long>();
            [JsonProperty("importedPaths")] public List<string> ImportedPaths = new List<string>();
            [JsonProperty("status")] public string Status;
            [JsonProperty("lastPolledAt")] public DateTime LastPolledAt;
        }

        sealed class State
        {
            [JsonProperty("runs")] public List<Followed> Runs = new List<Followed>();
            [JsonProperty("pipelines")] public List<long> Pipelines = new List<long>();
        }

        static State _state = new State();
        static readonly Dictionary<long, PipelineRun> Latest = new Dictionary<long, PipelineRun>();
        static readonly Dictionary<long, List<RunSummary>> LatestLists = new Dictionary<long, List<RunSummary>>();
        static CancellationTokenSource _cancel;

        /// <summary>A followed run was read again (every poll that returns it).</summary>
        public static event Action<PipelineRun> RunUpdated;

        /// <summary>
        /// A followed run is over — done, stopped (failed) or cancelled — with the paths imported for it. Raised while the
        /// run is still followed, so a handler may read <see cref="AgentSessionIdOf"/>; it is forgotten right after.
        /// </summary>
        public static event Action<PipelineRun, IReadOnlyList<string>> RunFinished;

        /// <summary>A followed run now waits for the user (a step waiting for its Run, or parts to choose).</summary>
        public static event Action<PipelineRun> RunNeedsUser;

        /// <summary>A followed pipeline's runs list was read again.</summary>
        public static event Action<long, List<RunSummary>> RunsUpdated;

        /// <summary>An import of a step failed (the run carries on).</summary>
        public static event Action<PipelineRun, PipelineRunStep, Exception> ImportFailed;

        static RunFollower()
        {
            try
            {
                _state = JsonConvert.DeserializeObject<State>(SessionState.GetString(Key, "{}")) ?? new State();
            }
            catch (Exception)
            {
                _state = new State();
            }
            AssemblyReloadEvents.beforeAssemblyReload += () => _cancel?.Cancel();
            EditorApplication.delayCall += Resume;
        }

        static void Save() => SessionState.SetString(Key, JsonConvert.SerializeObject(_state));

        /// <summary>Every run being followed.</summary>
        public static IReadOnlyList<Followed> Runs => _state.Runs;

        /// <summary>The last read of a run, if it has been read since the last reload.</summary>
        public static PipelineRun LatestRun(long runId) => Latest.TryGetValue(runId, out var run) ? run : null;

        /// <summary>The last read of a pipeline's runs list.</summary>
        public static List<RunSummary> LatestRuns(long pipelineId) => LatestLists.TryGetValue(pipelineId, out var list) ? list : null;

        public static bool IsFollowing(long runId) => _state.Runs.Any(r => r.RunId == runId);

        /// <summary>The agent session that started a followed run (0 when the user did, or it is not followed).</summary>
        public static long AgentSessionIdOf(long runId) => _state.Runs.FirstOrDefault(r => r.RunId == runId)?.AgentSessionId ?? 0;

        /// <summary>
        /// Follows a run until it is over. Following one already followed updates its options (the import list is kept).
        /// Reads it at once.
        /// </summary>
        public static void Follow(long runId, long pipelineId = 0, long agentSessionId = 0, RunImportMode import = RunImportMode.None, string folder = null)
        {
            var entry = _state.Runs.FirstOrDefault(r => r.RunId == runId);
            if (entry == null)
            {
                entry = new Followed { RunId = runId };
                _state.Runs.Add(entry);
            }
            if (pipelineId != 0) entry.PipelineId = pipelineId;
            if (agentSessionId != 0) entry.AgentSessionId = agentSessionId;
            entry.Import = import;
            entry.Folder = folder;
            entry.LastPolledAt = DateTime.MinValue;
            Save();
            Resume();
        }

        public static void Unfollow(long runId)
        {
            _state.Runs.RemoveAll(r => r.RunId == runId);
            Latest.Remove(runId);
            Save();
        }

        /// <summary>Polls a pipeline's runs list while any of its runs is live (the Runs view).</summary>
        public static void FollowRuns(long pipelineId)
        {
            if (!_state.Pipelines.Contains(pipelineId)) _state.Pipelines.Add(pipelineId);
            Save();
            Resume();
        }

        public static void UnfollowRuns(long pipelineId)
        {
            _state.Pipelines.Remove(pipelineId);
            LatestLists.Remove(pipelineId);
            Save();
        }

        /// <summary>Reads a followed run (or any run) now, outside the 3 s rhythm — e.g. right after an action.</summary>
        public static async Task<PipelineRun> RefreshAsync(long runId)
        {
            var run = await PipelinesApi.GetRunAsync(runId);
            var entry = _state.Runs.FirstOrDefault(r => r.RunId == runId);
            if (entry != null) await Handle(entry, run);
            else
            {
                Latest[runId] = run;
                RunUpdated?.Invoke(run);
            }
            Resume();
            return run;
        }

        /// <summary>Hands a run the caller has just read (an action's answer) to the follower, as if polled.</summary>
        public static Task Report(PipelineRun run)
        {
            var entry = run == null ? null : _state.Runs.FirstOrDefault(r => r.RunId == run.Id);
            if (entry == null) return Task.CompletedTask;
            Resume();
            return Handle(entry, run);
        }

        static void Resume()
        {
            if (_cancel != null && !_cancel.IsCancellationRequested) return;
            if (_state.Runs.Count == 0 && _state.Pipelines.Count == 0) return;
            _cancel = new CancellationTokenSource();
            _ = Poll(_cancel.Token);
        }

        static async Task Poll(CancellationToken cancel)
        {
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    if (_state.Runs.Count == 0 && _state.Pipelines.Count == 0) break;
                    if (!ZettaAuth.IsSignedIn)
                    {
                        await Task.Delay(PollMs, cancel);
                        continue;
                    }

                    foreach (var entry in _state.Runs.ToList())
                    {
                        if (cancel.IsCancellationRequested) break;
                        var due = entry.Status == RunStatuses.Draft ? DraftPollMs : PollMs - 250;
                        if ((DateTime.UtcNow - entry.LastPolledAt).TotalMilliseconds < due) continue;
                        try
                        {
                            var run = await PipelinesApi.GetRunAsync(entry.RunId, cancel);
                            await Handle(entry, run);
                        }
                        catch (ZettaApiException e) when (e.Status == 404)
                        {
                            Unfollow(entry.RunId);
                        }
                        catch (ZettaApiException)
                        {
                            // Transient (offline, 5xx): next round.
                        }
                    }

                    foreach (var pipelineId in _state.Pipelines.ToList())
                    {
                        if (cancel.IsCancellationRequested) break;
                        try
                        {
                            var list = await PipelinesApi.RunsAsync(pipelineId);
                            LatestLists[pipelineId] = list;
                            RunsUpdated?.Invoke(pipelineId, list);
                            // The list is polled only while something in it moves.
                            if (!list.Any(r => r.IsLive)) UnfollowRuns(pipelineId);
                        }
                        catch (ZettaApiException e) when (e.Status == 404)
                        {
                            UnfollowRuns(pipelineId);
                        }
                        catch (ZettaApiException)
                        {
                        }
                    }

                    await Task.Delay(PollMs, cancel);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Zetta 3D] Following pipeline runs stopped: {e.Message}");
            }
            finally
            {
                _cancel = null;
            }
        }

        static async Task Handle(Followed entry, PipelineRun run)
        {
            if (run == null) return;
            var before = entry.Status;
            entry.Status = run.Status;
            entry.LastPolledAt = DateTime.UtcNow;
            if (entry.PipelineId == 0) entry.PipelineId = run.PipelineId;
            Latest[run.Id] = run;
            Save();
            RunUpdated?.Invoke(run);

            if (entry.Import != RunImportMode.None) await ImportFinishedSteps(entry, run);

            if (run.Status == RunStatuses.Awaiting && before != RunStatuses.Awaiting) RunNeedsUser?.Invoke(run);

            if (run.Status is RunStatuses.Done or RunStatuses.Failed or RunStatuses.Cancelled)
            {
                try
                {
                    RunFinished?.Invoke(run, entry.ImportedPaths.ToList());
                }
                finally
                {
                    Unfollow(run.Id);
                }
            }
        }

        static async Task ImportFinishedSteps(Followed entry, PipelineRun run)
        {
            foreach (var step in run.ImportableSteps(entry.Import))
            {
                if (entry.ImportedSteps.Contains(step.Id)) continue;
                entry.ImportedSteps.Add(step.Id);
                Save();
                try
                {
                    var result = await ZettaImporter.ImportAsync(step.Generation, entry.Folder);
                    entry.ImportedPaths.AddRange(result.AssetPaths);
                    Save();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Zetta 3D] A pipeline step finished, but its import failed: {e.Message}");
                    ImportFailed?.Invoke(run, step, e);
                }
            }
        }

        /// <summary>
        /// Imports the given (or all final) mesh steps of a run now — for import_pipeline_results. Splats and steps that
        /// have not finished are skipped. Returns the imported asset paths.
        /// </summary>
        public static async Task<List<string>> ImportAsync(PipelineRun run, IEnumerable<string> nodeIds = null, string folder = null, ZettaImporter.Placement place = null)
        {
            var wanted = nodeIds?.ToList();
            var steps = run.ImportableSteps(wanted == null ? RunImportMode.FinalSteps : RunImportMode.AllMeshSteps)
                .Where(s => wanted == null || wanted.Contains(s.NodeId));
            var paths = new List<string>();
            foreach (var step in steps)
            {
                var result = await ZettaImporter.ImportAsync(step.Generation, folder, place);
                paths.AddRange(result.AssetPaths);
            }
            return paths;
        }
    }
}
