using System;
using UnityEngine;

namespace Zetta3D.Editor.Studio
{
    /// <summary>
    /// Seams between the Studio and other parts of the plugin that may or may not be present. The 3D Pipelines
    /// UI assigns <see cref="OpenPipelineRun"/>; until it does, a run opens on zetta3d.ai.
    /// </summary>
    public static class ZettaStudioHooks
    {
        /// <summary>Opens a pipeline run in the plugin's own Pipelines UI: (pipelineId, runId).</summary>
        public static Action<long, long> OpenPipelineRun;

        /// <summary>Opens a run — in the Pipelines UI when it is installed, otherwise on the site.</summary>
        public static void OpenRun(long pipelineId, long runId)
        {
            if (OpenPipelineRun != null)
            {
                OpenPipelineRun(pipelineId, runId);
                return;
            }
            Application.OpenURL($"{ZettaConfig.SiteBase}/pipelines?pipeline={pipelineId}&view=runs&run={runId}");
        }
    }
}
